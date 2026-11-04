using AwesomeAssertions;
using Xunit;

namespace SoftCal.Instruments.Phone.Tests;

/// <summary>
/// Finding services on the real local network with Windows' DNS-SD browsing, the way phones are found. They skip unless
/// SOFTCAL_NETWORK=1 (and need a network with something announcing itself: a printer, a speaker, a TV…).
/// </summary>
[Trait("Category", "Network")]
public sealed class NetworkTests
{
    /// <summary>Kinds of devices most homes and offices have one of.</summary>
    private static readonly string[] CommonServices =
    [
        "_ipp._tcp", "_printer._tcp", "_googlecast._tcp", "_spotify-connect._tcp", "_airplay._tcp", "_raop._tcp",
        "_sonos._tcp", "_hap._tcp", "_http._tcp", "_workstation._tcp", "_smb._tcp", "_device-info._tcp",
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_finder_lists_services_announced_on_the_network_with_their_address_and_port()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SOFTCAL_NETWORK") == "1", "Set SOFTCAL_NETWORK=1 to run network tests.");
        var finders = CommonServices.Select(type => (Type: type, Finder: new PhoneFinder(type))).ToList();
        try
        {
            foreach (var (_, finder) in finders)
            {
                finder.Start();
            }

            await Task.Delay(TimeSpan.FromSeconds(8), Token);

            var found = finders.SelectMany(f => f.Finder.Phones.Select(p => (f.Type, Service: p))).ToList();
            foreach (var (type, service) in found)
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"{type}: {service.Name} host={service.HostName} port={service.Port} addresses={string.Join(", ", service.Addresses)} → {string.Join(", ", service.Endpoints)}");
            }

            found.Should().NotBeEmpty("something on the network announces itself");
            found.Should().OnlyContain(f => f.Service.Port > 0 && f.Service.Addresses.Count > 0 && f.Service.Endpoints.Any());
        }
        finally
        {
            foreach (var (_, finder) in finders)
            {
                finder.Dispose();
            }
        }
    }
}
