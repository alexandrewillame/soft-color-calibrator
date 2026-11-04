namespace SoftCal.Remote;

/// <summary>
/// Maps the phone's clock to the PC's from pings, as NTP does: the PC notes when a ping left and when its answer came
/// back, the phone its own time in between. The answer that came back fastest sits closest to the middle of its round
/// trip, so it gives the offset (to within half its round trip, a few milliseconds on a LAN). The last 16 pings count,
/// so a slow drift between the clocks is followed.
/// </summary>
public sealed class ClockSync
{
    private const int Window = 16;

    private readonly Queue<(double RoundTrip, double Offset)> _samples = new();
    private readonly object _gate = new();

    /// <summary>The phone's clock minus the PC's (seconds); null before the first ping came back.</summary>
    public double? Offset
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count == 0 ? null : _samples.MinBy(s => s.RoundTrip).Offset;
            }
        }
    }

    /// <summary>How far the offset can be off: half the fastest round trip.</summary>
    public double? Uncertainty
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count == 0 ? null : _samples.Min(s => s.RoundTrip) / 2;
            }
        }
    }

    public int Samples
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>One ping: when it left the PC, the phone's time in its answer, and when the answer arrived.</summary>
    public void Add(double pcSent, double phoneTime, double pcReceived)
    {
        var roundTrip = pcReceived - pcSent;
        if (roundTrip < 0 || double.IsNaN(phoneTime))
        {
            return;
        }

        lock (_gate)
        {
            _samples.Enqueue((roundTrip, phoneTime - ((pcSent + pcReceived) / 2)));
            while (_samples.Count > Window)
            {
                _samples.Dequeue();
            }
        }
    }

    /// <summary>A phone time on the PC's clock.</summary>
    public double ToPc(double phoneTime) => phoneTime - (Offset ?? 0);
}
