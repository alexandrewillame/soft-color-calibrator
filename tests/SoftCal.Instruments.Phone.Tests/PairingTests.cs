using System.Net.WebSockets;
using AwesomeAssertions;
using SoftCal.Remote;
using Xunit;

namespace SoftCal.Instruments.Phone.Tests;

/// <summary>Pairing with the code the phone shows: only a PC that knows it gets the camera, and only one at a time.</summary>
public sealed class PairingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_PC_with_the_right_code_connects_and_maps_the_phones_clock()
    {
        await using var phone = TestPhone.Start();

        await using var client = await phone.ConnectAsync(Token);

        client.Phone.Model.Should().Be("Phone");
        client.Capabilities.ManualSensor.Should().BeTrue();
        client.Clock.Samples.Should().BeGreaterThanOrEqualTo(6, "a burst of pings maps the clock at once");
        client.Clock.Offset.Should().BeApproximately(phone.Offset, 0.02);
    }

    [Fact]
    public async Task Typed_spaces_and_dashes_in_the_code_do_not_matter()
    {
        await using var phone = TestPhone.Start();
        var code = phone.Server.Code;

        await using var client = await phone.ConnectAsync(Token, $"{code[..3]} - {code[3..]}");

        client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task A_wrong_code_is_refused()
    {
        await using var phone = TestPhone.Start();

        var connect = () => phone.ConnectAsync(Token, TestPhone.Wrong(phone.Server.Code));

        await connect.Should().ThrowAsync<PhoneLinkException>().WithMessage("Wrong code*");
        phone.Camera.Prepares.Should().Be(0);
    }

    [Fact]
    public async Task Five_wrong_codes_make_the_phone_show_a_new_one()
    {
        await using var phone = TestPhone.Start();
        var first = phone.Server.Code;

        for (var i = 0; i < Pairing.MaxFailures; i++)
        {
            var connect = () => phone.ConnectAsync(Token, TestPhone.Wrong(first));
            await connect.Should().ThrowAsync<PhoneLinkException>();
        }

        phone.Server.Code.Should().NotBe(first, "guessing must not get anywhere");
        var old = () => phone.ConnectAsync(Token, first);
        await old.Should().ThrowAsync<PhoneLinkException>();
        await using var client = await phone.ConnectAsync(Token);
        client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task A_second_PC_is_told_the_phone_is_busy_and_the_first_keeps_it()
    {
        await using var phone = TestPhone.Start();
        await using var first = await phone.ConnectAsync(Token);

        var second = () => phone.ConnectAsync(Token);

        await second.Should().ThrowAsync<PhoneLinkException>().WithMessage("*already connected to another PC*");
        first.IsConnected.Should().BeTrue();
        (await first.SetExposureAsync(1.0 / 64, Token)).Nominal.Should().BeApproximately(1.0 / 64, 1e-12);
    }

    [Fact]
    public async Task The_phone_is_free_again_when_the_PC_leaves()
    {
        await using var phone = TestPhone.Start();
        var first = await phone.ConnectAsync(Token);
        await first.DisposeAsync();

        await WaitUntil(() => phone.Camera.Restores > 0);
        await using var second = await phone.ConnectAsync(Token);

        second.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task A_PC_speaking_another_version_of_the_link_is_told_so()
    {
        await using var phone = TestPhone.Start();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(phone.Address, Token);
        using var link = new LinkChannel(socket);

        await link.SendAsync(new HelloMessage(PhoneLink.Protocol + 1, "Future PC", Pairing.NewNonce()), Token);
        var answer = await link.ReceiveAsync(Token);

        answer!.Message.Should().BeOfType<ErrorMessage>().Which.Message.Should().Contain("update the older one");
    }

    [Fact]
    public async Task Something_else_than_the_link_on_the_port_gets_a_plain_refusal()
    {
        await using var phone = TestPhone.Start();
        using var http = new HttpClient();

        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{phone.Server.Port}/"), Token);

        ((int)response.StatusCode).Should().Be(400);
    }

    [Fact]
    public void Proofs_depend_on_the_code_the_side_and_both_nonces()
    {
        var key = Pairing.Key("123456");
        var pc = Pairing.PcProof(key, "a", "b");

        Pairing.Same(pc, Pairing.PcProof(Pairing.Key("123 456"), "a", "b")).Should().BeTrue();
        Pairing.Same(pc, Pairing.PcProof(Pairing.Key("123457"), "a", "b")).Should().BeFalse();
        Pairing.Same(pc, Pairing.PhoneProof(key, "a", "b")).Should().BeFalse("the phone's proof cannot be replayed as the PC's");
        Pairing.Same(pc, Pairing.PcProof(key, "a", "c")).Should().BeFalse();
        Pairing.Same(pc, null).Should().BeFalse();
    }

    [Fact]
    public void Codes_are_six_digits()
    {
        for (var i = 0; i < 50; i++)
        {
            Pairing.NewCode().Should().MatchRegex("^[0-9]{6}$");
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, Token);
        }

        condition().Should().BeTrue();
    }
}
