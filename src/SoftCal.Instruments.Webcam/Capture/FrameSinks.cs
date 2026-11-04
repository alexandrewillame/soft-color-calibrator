using System.Threading.Channels;
using SoftCal.Core.Instruments;
using SoftCal.Instruments.Webcam.Frames;

namespace SoftCal.Instruments.Webcam.Capture;

/// <summary>A frame's statistics plus the webcam-specific details kept for diagnostics.</summary>
public sealed record WebcamFrame(
    FrameStats Stats,
    double MeanY,
    double MeanCb,
    double MeanCr,
    int MinLuma,
    int MaxLuma,
    double ArrivalSeconds,
    double? MetadataExposureSeconds);

/// <summary>Computes ROI statistics for every frame and queues them for one consumer.</summary>
internal sealed class StatisticsSink : IFrameSink
{
    private readonly Channel<WebcamFrame> _channel = Channel.CreateBounded<WebcamFrame>(
        new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });

    private readonly RoiSpec _roi;
    private readonly Func<int> _epoch;
    private readonly Func<(YcbcrDecoder Decoder, ClipThresholds Clip)> _decoding;
    private PixelRect? _rect;
    private volatile bool _closed;

    public StatisticsSink(RoiSpec roi, Func<int> epoch, Func<(YcbcrDecoder, ClipThresholds)> decoding)
    {
        _roi = roi;
        _epoch = epoch;
        _decoding = decoding;
    }

    public ChannelReader<WebcamFrame> Reader => _channel.Reader;

    public bool OnFrame(in CapturedFrame frame)
    {
        if (_closed)
        {
            return false;
        }

        _rect ??= PixelRect.FromRoi(_roi, frame.Geometry.Width, frame.Geometry.Height);
        var (decoder, clip) = _decoding();
        var s = FrameStatisticsCalculator.Compute(frame.Buffer, frame.Geometry, _rect.Value, _roi.RowBands, decoder, clip);
        var stats = new FrameStats(frame.Sequence, frame.TimestampSeconds, _epoch(), s.Red, s.Green, s.Blue, s.PixelCount, s.RowBandMeans);
        _channel.Writer.TryWrite(new WebcamFrame(stats, s.MeanY, s.MeanCb, s.MeanCr, s.MinLuma, s.MaxLuma, frame.ArrivalSeconds, frame.MetadataExposureSeconds));
        return true;
    }

    public void Close(Exception? error = null)
    {
        _closed = true;
        _channel.Writer.TryComplete(error);
    }
}

/// <summary>Averages the luma of a region over a number of frames.</summary>
internal sealed class LumaImageSink : IFrameSink
{
    private readonly RoiSpec _roi;
    private readonly int _frames;
    private readonly int _skip;
    private readonly Func<YcbcrDecoder> _decoder;
    private readonly TaskCompletionSource<RoiImage> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PixelRect? _rect;
    private double[]? _sum;
    private int _seen;
    private int _count;
    private double _first;
    private double _last;

    public LumaImageSink(RoiSpec roi, int frames, int skip, Func<YcbcrDecoder> decoder)
    {
        _roi = roi;
        _frames = frames;
        _skip = skip;
        _decoder = decoder;
    }

    public Task<RoiImage> Completion => _completion.Task;

    public bool OnFrame(in CapturedFrame frame)
    {
        if (_completion.Task.IsCompleted)
        {
            return false;
        }

        if (_seen++ < _skip)
        {
            return true;
        }

        var rect = _rect ??= PixelRect.FromRoi(_roi, frame.Geometry.Width, frame.Geometry.Height);
        _sum ??= new double[rect.Width * rect.Height];
        FrameStatisticsCalculator.AddLuma(frame.Buffer, frame.Geometry, rect, _decoder(), _sum);
        if (_count == 0)
        {
            _first = frame.TimestampSeconds;
        }

        _last = frame.TimestampSeconds;
        if (++_count < _frames)
        {
            return true;
        }

        var pixels = new double[_sum.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = _sum[i] / _count;
        }

        _completion.TrySetResult(new RoiImage(rect.Width, rect.Height, pixels, _count, _first, _last));
        return false;
    }

    public void Fail(Exception error) => _completion.TrySetException(error);

    public void Cancel() => _completion.TrySetCanceled();
}
