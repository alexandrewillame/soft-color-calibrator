using System.Diagnostics;
using System.Runtime.InteropServices;
using SoftCal.Instruments.Webcam.Frames;
using SoftCal.Instruments.Webcam.Interop;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SoftCal.Instruments.Webcam.Capture;

/// <summary>Where a frame's timestamp came from.</summary>
public enum TimestampSource
{
    /// <summary>MFSampleExtension_DeviceTimestamp (QPC, 100 ns).</summary>
    DeviceTimestamp,

    /// <summary>The source reader's sample time, when it is on the QPC clock.</summary>
    SampleTime,

    /// <summary>The host QPC when ReadSample returned (fallback; includes transfer delay).</summary>
    Arrival,
}

/// <summary>One frame as it leaves the source reader. The buffer is only valid during the callback.</summary>
public readonly ref struct CapturedFrame
{
    public CapturedFrame(ReadOnlySpan<byte> buffer, FrameGeometry geometry, long sequence, double timestampSeconds, double arrivalSeconds, double? metadataExposureSeconds)
    {
        Buffer = buffer;
        Geometry = geometry;
        Sequence = sequence;
        TimestampSeconds = timestampSeconds;
        ArrivalSeconds = arrivalSeconds;
        MetadataExposureSeconds = metadataExposureSeconds;
    }

    public ReadOnlySpan<byte> Buffer { get; }

    public FrameGeometry Geometry { get; }

    public long Sequence { get; }

    /// <summary>Capture time in QPC seconds (see <see cref="CaptureLoop.TimestampSource"/>).</summary>
    public double TimestampSeconds { get; }

    /// <summary>Host QPC seconds when the frame was handed to us.</summary>
    public double ArrivalSeconds { get; }

    /// <summary>Exposure time from the frame's capture metadata, when the driver provides it.</summary>
    public double? MetadataExposureSeconds { get; }
}

/// <summary>Receives frames on the capture thread. Implementations must be fast and must not keep the buffer.</summary>
internal interface IFrameSink
{
    /// <summary>Returns false to be removed.</summary>
    bool OnFrame(in CapturedFrame frame);
}

/// <summary>
/// Pulls samples from a synchronous source reader on a dedicated thread, hands each locked buffer to the registered
/// sinks and releases the sample at once (the device source recycles a small sample pool).
/// </summary>
internal sealed class CaptureLoop : IDisposable
{
    private static readonly uint VideoStream = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM);

    private readonly IMFSourceReader _reader;
    private readonly NativeFormat _format;
    private readonly Lock _gate = new();
    private readonly List<IFrameSink> _sinks = [];
    private readonly Thread _thread;
    private volatile bool _stopping;
    private long _sequence;
    private long _lastFrameTicks;
    private TimestampSource? _timestampSource;

    public CaptureLoop(IMFSourceReader reader, NativeFormat format)
    {
        _reader = reader;
        _format = format;
        _thread = new Thread(Run) { IsBackground = true, Name = "SoftCal webcam capture" };
    }

    /// <summary>Raised on the capture thread when the stream fails or ends; the loop then stops.</summary>
    public event Action<Exception>? Failed;

    public long FramesCaptured => Interlocked.Read(ref _sequence);

    public TimestampSource? TimestampSource => _timestampSource;

    /// <summary>QPC seconds of the last frame's arrival (0 before the first frame).</summary>
    public double LastFrameArrivalSeconds => Volatile.Read(ref _lastFrameTicks) / (double)Stopwatch.Frequency;

    public bool IsRunning => _thread.IsAlive && !_stopping;

    public void Start() => _thread.Start();

    public void AddSink(IFrameSink sink)
    {
        lock (_gate)
        {
            _sinks.Add(sink);
        }
    }

    public void RemoveSink(IFrameSink sink)
    {
        lock (_gate)
        {
            _sinks.Remove(sink);
        }
    }

    /// <summary>Stops the loop; false when the thread is still blocked in ReadSample after the timeout (the reader must
    /// then be leaked rather than released under it).</summary>
    public bool Stop(TimeSpan timeout)
    {
        _stopping = true;
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            return _thread.Join(timeout);
        }

        return true;
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(3));

    private unsafe void Run()
    {
        try
        {
            while (!_stopping)
            {
                _reader.ReadSample(VideoStream, 0, out _, out var flags, out var sampleTime, out var sample);
                var arrival = Stopwatch.GetTimestamp();
                try
                {
                    var f = (MF_SOURCE_READER_FLAG)flags;
                    if ((f & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
                    {
                        throw new InvalidOperationException("The camera stream reported an error.");
                    }

                    if ((f & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                    {
                        throw new InvalidOperationException("The camera stream ended (device removed or taken over).");
                    }

                    if ((f & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
                    {
                        throw new InvalidOperationException("The camera changed its media type during the measurement.");
                    }

                    if (sample is null || _stopping)
                    {
                        continue;
                    }

                    Deliver(sample, sampleTime, arrival);
                }
                finally
                {
                    MfInterop.Release(sample);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            if (!_stopping)
            {
                _stopping = true;
                Failed?.Invoke(ex);
            }
        }
    }

    private unsafe void Deliver(IMFSample sample, long sampleTime100ns, long arrivalTicks)
    {
        var arrivalSeconds = arrivalTicks / (double)Stopwatch.Frequency;
        var timestamp = ChooseTimestamp(sample, sampleTime100ns, arrivalSeconds);
        var exposure = ReadMetadataExposure(sample);

        sample.GetBufferByIndex(0, out var buffer);
        try
        {
            byte* data;
            int stride;
            int length;
            var twoD = buffer as IMF2DBuffer;
            if (twoD is not null)
            {
                twoD.Lock2D(out data, out stride);
                length = Math.Abs(stride) * (_format.Layout == PixelLayout.Nv12 ? _format.Height * 3 / 2 : _format.Height);
            }
            else
            {
                buffer.Lock(out data, out _, out var current);
                length = (int)current;
                stride = _format.DefaultStride ?? (_format.Layout == PixelLayout.Yuy2 ? _format.Width * 2 : _format.Width);
            }

            try
            {
                if (stride <= 0)
                {
                    throw new InvalidOperationException("Bottom-up camera frames are not supported.");
                }

                var geometry = new FrameGeometry(_format.Width, _format.Height, stride, _format.Layout!.Value);
                var span = new ReadOnlySpan<byte>(data, length);
                var sequence = Interlocked.Increment(ref _sequence) - 1;
                Volatile.Write(ref _lastFrameTicks, arrivalTicks);
                var frame = new CapturedFrame(span, geometry, sequence, timestamp, arrivalSeconds, exposure);
                IFrameSink[] sinks;
                lock (_gate)
                {
                    sinks = [.. _sinks];
                }

                foreach (var sink in sinks)
                {
                    if (!sink.OnFrame(frame))
                    {
                        RemoveSink(sink);
                    }
                }
            }
            finally
            {
                if (twoD is not null)
                {
                    twoD.Unlock2D();
                }
                else
                {
                    buffer.Unlock();
                }
            }
        }
        finally
        {
            MfInterop.Release(buffer);
        }
    }

    /// <summary>
    /// Prefers the device timestamp, then the sample time, when either is on the host's QPC clock (100 ns units, a few
    /// hundred milliseconds or less before arrival); otherwise falls back to the arrival time. The choice is made on the
    /// first frame and kept, so timestamps never switch clocks mid-run.
    /// </summary>
    private double ChooseTimestamp(IMFSample sample, long sampleTime100ns, double arrivalSeconds)
    {
        double? device = null;
        try
        {
            sample.GetUINT64(PInvoke.MFSampleExtension_DeviceTimestamp, out var value);
            device = value * 1e-7;
        }
        catch (COMException)
        {
        }

        var sampleSeconds = sampleTime100ns * 1e-7;
        static bool OnQpc(double t, double arrival) => arrival - t is >= -0.005 and < 1.0;

        _timestampSource ??= device is { } first && OnQpc(first, arrivalSeconds)
            ? Capture.TimestampSource.DeviceTimestamp
            : OnQpc(sampleSeconds, arrivalSeconds) ? Capture.TimestampSource.SampleTime : Capture.TimestampSource.Arrival;

        return _timestampSource switch
        {
            Capture.TimestampSource.DeviceTimestamp when device is { } d => d,
            Capture.TimestampSource.SampleTime => sampleSeconds,
            _ => arrivalSeconds,
        };
    }

    private static double? ReadMetadataExposure(IMFSample sample)
    {
        try
        {
            sample.GetUnknown<IMFAttributes>(PInvoke.MFSampleExtension_CaptureMetadata, out var metadata);
            try
            {
                return MfInterop.GetUInt64(metadata, PInvoke.MF_CAPTURE_METADATA_EXPOSURE_TIME) is { } exposure100ns ? exposure100ns * 1e-7 : null;
            }
            finally
            {
                MfInterop.Release(metadata);
            }
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }
}
