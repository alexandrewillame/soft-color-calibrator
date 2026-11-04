using AwesomeAssertions;
using SoftCal.Core.Analysis;
using SoftCal.Core.Color;
using Xunit;

namespace SoftCal.Core.Tests.Analysis;

public sealed class TintTests
{
    [Theory]
    [InlineData(1.05, 1.00, 1.00, "reddish")]
    [InlineData(1.00, 1.00, 0.95, "yellowish")]
    [InlineData(1.00, 1.05, 1.00, "greenish")]
    [InlineData(0.95, 1.00, 1.00, "cyan")]
    [InlineData(1.00, 1.00, 1.05, "bluish")]
    [InlineData(1.00, 0.95, 1.00, "magenta")]
    public void Each_channel_up_or_down_has_its_name(double r, double g, double b, string expected)
    {
        DisplayAnalysis.Tint(Normalized(r, g, b)).Should().Be(expected);
    }

    /// <summary>The BenQ's code-72 gray: red up 2.5 %, blue down 3.5 %. It was called "reddish" because red was
    /// checked first; the blue deficit makes it about as yellow as red.</summary>
    [Fact]
    public void Red_up_and_blue_further_down_reads_as_yellowish_not_reddish()
    {
        DisplayAnalysis.Tint(new Vec3(1.0252, 1.0095, 0.9653)).Should().Be("yellowish");
    }

    [Fact]
    public void Deviations_within_the_threshold_have_no_name()
    {
        DisplayAnalysis.Tint(new Vec3(1.015, 1.0, 0.985)).Should().BeNull();
        DisplayAnalysis.Tint(new Vec3(1.03, 1.0, 0.97), threshold: 0.05).Should().BeNull();
    }

    private static Vec3 Normalized(double r, double g, double b)
    {
        var v = new Vec3(r, g, b);
        return v / (v.Sum / 3);
    }
}
