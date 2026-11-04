using System.Reflection;
using System.Runtime.InteropServices;
using AwesomeAssertions;
using SoftCal.Core.Measurement;
using SoftCal.Windows.Ddc;
using SoftCal.Windows.Displays;
using SoftCal.Windows.Gpu;
using SoftCal.Windows.Patches;
using Xunit;

namespace SoftCal.Windows.Tests;

public sealed class PatchAndProbeTests
{
    [Fact]
    public void The_default_patch_is_a_centred_square_of_60_percent_of_the_height()
    {
        var rect = PatchRasterizer.Rect(PatchRasterizer.DefaultLayout, 1920, 1080);

        rect.Should().Be(new PatchRect(636, 216, 648, 648));
        PatchRasterizer.Rect(new PatchLayout(0.5, 0.5, 2.0), 1920, 1080).Should().Be(new PatchRect(0, 0, 1920, 1080));
    }

    [Fact]
    public void Halftone_phases_are_tied_to_the_display_grid()
    {
        var checker = new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard);
        var rows = new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.HorizontalLines);
        var columns = new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.VerticalLines);

        PatchRasterizer.IsOn(checker, 0, 0).Should().BeTrue();
        PatchRasterizer.IsOn(checker, 1, 0).Should().BeFalse();
        PatchRasterizer.IsOn(checker, 1, 1).Should().BeTrue();
        PatchRasterizer.IsOn(rows, 5, 2).Should().BeTrue();
        PatchRasterizer.IsOn(rows, 5, 3).Should().BeFalse();
        PatchRasterizer.IsOn(columns, 4, 9).Should().BeTrue();
        PatchRasterizer.IsOn(columns, 3, 9).Should().BeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void Bayer_dithers_have_exactly_the_requested_fraction(int sixteenths)
    {
        var bayer = new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, sixteenths);

        PatchRasterizer.OnFraction(bayer, new PatchRect(636, 216, 648, 648)).Should().Be(sixteenths / 16.0);
    }

    [Fact]
    public void Bayer_8_of_16_is_the_checkerboard()
    {
        var bayer = new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Bayer4x4, 8);
        var checker = new HalftonePatch(DeviceRgb.White, DeviceRgb.Black, HalftoneGeometry.Checkerboard);

        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                PatchRasterizer.IsOn(bayer, x, y).Should().Be(PatchRasterizer.IsOn(checker, x, y));
            }
        }
    }

    [Fact]
    public void Rendering_writes_bgra_with_the_surround_outside_the_patch()
    {
        var pixels = new byte[8 * 4 * 4];
        var patch = new SolidPatch(new DeviceRgb(10, 20, 30)) { Layout = new PatchLayout(0.5, 0.5, 0.5, new DeviceRgb(1, 2, 3)) };

        var rect = PatchRasterizer.Render(patch, 8, 4, pixels);

        rect.Should().Be(new PatchRect(3, 1, 2, 2));
        pixels.AsSpan(((1 * 8) + 3) * 4, 4).ToArray().Should().Equal(30, 20, 10, 255);
        pixels.AsSpan(0, 4).ToArray().Should().Equal(3, 2, 1, 255);
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#BNQ7F2B#5&3791d1ef&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", @"SYSTEM\CurrentControlSet\Enum\DISPLAY\BNQ7F2B\5&3791d1ef&0&UID4352\Device Parameters")]
    [InlineData("", null)]
    [InlineData(@"\\?\USB#VID_1234", null)]
    public void A_monitor_device_path_maps_to_its_edid_registry_key(string path, string? key) =>
        MonitorDevicePath.ToDeviceParametersKey(path).Should().Be(key);

    [Fact]
    public void Nvapi_color_data_matches_the_headers_layout()
    {
        Marshal.SizeOf<NvApiProbe.ColorDataV5>().Should().Be(24);
        Marshal.OffsetOf<NvApiProbe.ColorDataV5>(nameof(NvApiProbe.ColorDataV5.Bpc)).Should().Be(12);
        NvApiProbe.MakeVersion(24, 5).Should().Be(0x00050018u);
        NvApiProbe.MakeVersion(20, 4).Should().Be(0x00040014u);
    }

    [Fact]
    public void Nvapi_values_are_named_and_full_range_rgb_is_recognized()
    {
        var full = NvApiProbe.Describe(new NvApiProbe.ColorDataV5 { ColorFormat = 0, Colorimetry = 0, DynamicRange = 0, Bpc = 2, SelectionPolicy = 1, Depth = 1 }, 5);
        var limited = NvApiProbe.Describe(new NvApiProbe.ColorDataV5 { ColorFormat = 0, DynamicRange = 1, Bpc = 2 }, 5);
        var ycc = NvApiProbe.Describe(new NvApiProbe.ColorDataV5 { ColorFormat = 1, Colorimetry = 2 }, 4);

        full.IsFullRangeRgb.Should().BeTrue();
        full.BitsPerComponent.Should().Be("8");
        full.DesktopDepth.Should().Be("8 bpc");
        limited.IsFullRangeRgb.Should().BeFalse();
        limited.DynamicRange.Should().Be("limited (CEA)");
        ycc.ColorFormat.Should().Be("YCbCr 4:2:2");
        ycc.DesktopDepth.Should().BeNull("version 4 has no desktop depth");
    }

    [Fact]
    public void Vcp_values_are_interpreted_where_mccs_defines_them()
    {
        DdcCiProbe.Interpret(0x14, 5, 12).Should().Be("6500 K");
        DdcCiProbe.Interpret(0x72, 120, 160).Should().Contain("2.20");
        DdcCiProbe.Interpret(0x10, 18, 100).Should().Be("18% of range");
        DdcCiProbe.Codes.Select(c => c.Code).Should().Equal(0x10, 0x12, 0x14, 0x16, 0x18, 0x1A, 0x6C, 0x6E, 0x70, 0x72, 0xDC);
    }

    /// <summary>The capabilities request can crash Windows on a malformed reply: the binding must not even exist.</summary>
    [Fact]
    public void The_ddc_capabilities_request_is_never_bound()
    {
        var methods = typeof(DdcCiProbe).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        methods.Should().Contain("GetVCPFeatureAndVCPFeatureReply");
        methods.Should().NotContain("CapabilitiesRequestAndCapabilitiesReply");
        methods.Should().NotContain("GetCapabilitiesStringLength");
    }
}
