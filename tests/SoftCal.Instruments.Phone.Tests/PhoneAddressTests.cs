using System.Text.Json;
using AwesomeAssertions;
using SoftCal.Remote;
using Xunit;

namespace SoftCal.Instruments.Phone.Tests;

public sealed class PhoneAddressTests
{
    [Theory]
    [InlineData("192.168.1.23", "ws://192.168.1.23:47630/softcal")]
    [InlineData(" 192.168.1.23:5000 ", "ws://192.168.1.23:5000/softcal")]
    [InlineData("ws://10.0.0.2:47630/softcal", "ws://10.0.0.2:47630/softcal")]
    [InlineData("[fe80::1]:5000", "ws://[fe80::1]:5000/softcal")]
    [InlineData("fe80::1", "ws://[fe80::1]:47630/softcal")]
    [InlineData("pixel-7.local", "ws://pixel-7.local:47630/softcal")]
    public void Typed_addresses_become_the_links_address(string typed, string expected)
    {
        PhoneAddress.Parse(typed).Should().Be(new Uri(expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.1.23:")]
    [InlineData("192.168.1.23:99999")]
    [InlineData("phone:abc")]
    [InlineData("[fe80::1")]
    [InlineData("not an address")]
    public void Anything_else_is_no_address(string typed)
    {
        PhoneAddress.Parse(typed).Should().BeNull();
    }

    [Theory]
    [InlineData("Google", "Pixel 7", "Google Pixel 7")]
    [InlineData("samsung", "SM-S911B", "Samsung SM-S911B")]
    [InlineData("Xiaomi", "Xiaomi 13", "Xiaomi 13")]
    public void Phones_are_named_as_their_owners_know_them(string manufacturer, string model, string expected)
    {
        PhoneInstrument.Name(new PhoneDescription(manufacturer, model, "SoftCal Camera")).Should().Be(expected);
    }

    [Fact]
    public void Exposure_steps_are_doublings_up_to_a_quarter_second()
    {
        var steps = PhoneInstrument.Steps(1.0 / 100_000, 30);

        steps[0].Should().Be(Math.Pow(2, -16));
        steps[^1].Should().Be(0.25);
        steps.Should().HaveCount(15);
        PhoneInstrument.Steps(0, 1).Should().BeEmpty();
    }

    [Fact]
    public void Messages_travel_as_camelCase_json_with_their_type()
    {
        LinkMessage frame = new FrameMessage(7, 12.5, 3, 0.01, new ChannelSummary(1, 2, 0), new ChannelSummary(3, 4, 0), new ChannelSummary(5, 6, 0.5), 100, [1.5, 2.5]);

        var json = JsonSerializer.Serialize(frame, LinkJson.Default.LinkMessage);
        var back = JsonSerializer.Deserialize(json, LinkJson.Default.LinkMessage);

        json.Should().StartWith("{\"type\":\"frame\"").And.Contain("\"seq\":7").And.Contain("\"clipped\":0.5");
        back.Should().BeOfType<FrameMessage>().Which.Bands.Should().Equal(1.5, 2.5);
        JsonSerializer.Deserialize("{\"seq\":1,\"type\":\"ok\",\"id\":4}", LinkJson.Default.LinkMessage)
            .Should().Be(new OkMessage(4), "the type may come anywhere in the object");
    }
}
