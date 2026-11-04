using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.System.Threading;

namespace SoftCal.Windows.Power;

/// <summary>
/// Keeps the display on and the system awake for as long as it is held, through a named power request (it shows in
/// <c>powercfg /requests</c> with its reason). Unlike SetThreadExecutionState it does not depend on which thread holds it.
/// </summary>
public sealed class DisplayKeepAwake : IDisposable
{
    private readonly SafeFileHandle? _request;
    private bool _disposed;

    private DisplayKeepAwake(SafeFileHandle? request) => _request = request;

    /// <summary>False when Windows refused the power request; the measurement can still run, but the screen might dim.</summary>
    public bool IsActive => _request is not null && !_disposed;

    public static unsafe DisplayKeepAwake Acquire(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        fixed (char* text = reason)
        {
            var context = new REASON_CONTEXT
            {
                Version = PInvoke.POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_FLAGS.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            };
            context.Reason.SimpleReasonString = new PWSTR(text);
            var handle = PInvoke.PowerCreateRequest(in context);
            if (!handle.IsInvalid
                && PInvoke.PowerSetRequest(handle, POWER_REQUEST_TYPE.PowerRequestDisplayRequired)
                && PInvoke.PowerSetRequest(handle, POWER_REQUEST_TYPE.PowerRequestSystemRequired))
            {
                return new DisplayKeepAwake(handle);
            }

            handle.Dispose();
            return new DisplayKeepAwake(null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_request is not null)
        {
            PInvoke.PowerClearRequest(_request, POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
            PInvoke.PowerClearRequest(_request, POWER_REQUEST_TYPE.PowerRequestSystemRequired);
            _request.Dispose();
        }
    }
}
