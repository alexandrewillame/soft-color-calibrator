using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoftCal.Core.Color;

namespace SoftCal.Core.Analysis;

/// <summary>Writes a <see cref="DisplayReport"/> as JSON (for re-analysis and tools) or as a self-contained HTML page.</summary>
public static class ReportWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(DisplayReport report) => JsonSerializer.Serialize(report, Json);

    public static DisplayReport? FromJson(string json) => JsonSerializer.Deserialize<DisplayReport>(json, Json);

    /// <summary>The raw readings, saved next to each report: analysis is a pure function of them, so a saved run can be
    /// re-analysed when the analysis improves.</summary>
    public static string MeasurementToJson(Measurement.DisplayMeasurement measurement) => JsonSerializer.Serialize(measurement, Json);

    public static Measurement.DisplayMeasurement? MeasurementFromJson(string json) => JsonSerializer.Deserialize<Measurement.DisplayMeasurement>(json, Json);

    public static string ToHtml(DisplayReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        static string E(string s) => WebUtility.HtmlEncode(s);
        string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
        var html = new StringBuilder();
        html.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Display report</title>
            <style>
            :root{--bg:#fff;--fg:#1c1c1e;--muted:#6b6b70;--line:#d8d8dc;--accent:#2f6fd6;--warn:#b26a00;--bad:#c0392b}
            @media (prefers-color-scheme:dark){:root{--bg:#18181a;--fg:#ececee;--muted:#9a9aa0;--line:#38383c;--accent:#79a6ff;--warn:#f0b04a;--bad:#ff7a6b}}
            body{background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,Segoe UI,sans-serif;margin:0;padding:24px 16px;max-width:860px;margin-inline:auto}
            h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px}.muted{color:var(--muted)}
            table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid var(--line);padding:6px 8px;text-align:left}
            .Info{border-left:4px solid var(--accent)}.Warning{border-left:4px solid var(--warn)}.Problem{border-left:4px solid var(--bad)}
            ul.f{list-style:none;padding:0}ul.f li{padding:6px 10px;margin:6px 0;background:color-mix(in srgb,var(--line) 25%,transparent)}
            .conf{font-size:12px;color:var(--muted)}svg{width:100%;height:auto}
            </style></head><body>
            """);
        var mode = string.IsNullOrWhiteSpace(report.Profile) ? string.Empty : $" — profile {E(report.Profile)}";
        html.Append(F($"<h1>{E(report.DisplayName)}{mode}</h1><div class=\"muted\">Measured {report.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} with {E(report.InstrumentModel)} in {report.MeasurementSeconds / 60:0.0} min</div>"));

        html.Append("<h2>Findings</h2><ul class=\"f\">");
        foreach (var f in report.Findings.OrderByDescending(f => f.Level))
        {
            html.Append(F($"<li class=\"{f.Level}\"><b>{E(f.Topic)}</b> — {E(f.Message)}</li>"));
        }

        html.Append("</ul><h2>Tone curve</h2>");
        html.Append(ToneSvg(report.Tone, 2.2));
        html.Append("<table>");
        Row(html, "Effective gamma (at 50 %)", F($"{report.Tone.EffectiveGamma:0.00}"), report.Tone.Confidence);
        Row(html, "Average gamma", F($"{report.Tone.AverageGamma:0.00}"), report.Tone.Confidence);
        Row(html, "Log-log slope (shadow-weighted)", F($"{report.Tone.LogLogSlope:0.00}"), report.Tone.Confidence);
        Row(html, "Deepest level measured", F($"code {report.Tone.DeepestMeasuredCode:0}"), report.Tone.Confidence);
        Row(html, "Channel gammas, each color alone (R, G, B)", F($"{report.Channels.ChannelGammas.X:0.00}, {report.Channels.ChannelGammas.Y:0.00}, {report.Channels.ChannelGammas.Z:0.00}"), Confidence.High);
        var midNoise = report.Channels.GrayLevels is { Count: > 0 } measured ? $" (± {Percent(measured[0].Uncertainty)})" : string.Empty;
        Row(html, "Mid-gray balance", Balance(report.Channels.MidGrayBalance, report.Channels.MidGrayTint, report.Channels.Confidence != Confidence.Low) + midNoise, report.Channels.Confidence);
        Row(html, "Black crush", report.Steps.BlackCrushUpToCode is { } c ? F($"codes 0–{c} identical") : "none", report.Steps.Confidence);
        Row(html, "White clipping", report.Steps.WhiteClipFromCode is { } w ? F($"codes {w}–255 identical") : "none", report.Steps.Confidence);
        Row(html, "Contrast", report.Contrast.ToString(), report.Contrast.Confidence);
        if (report.PatternSpreadCodes is { } spread)
        {
            Row(html, "Checkerboard vs lines", F($"{spread:0.00} codes"), Confidence.High);
        }

        if (report.Edid is { } edid)
        {
            Row(html, "White point", F($"{edid.WhiteCct:0} K, Δuv {edid.WhiteDuv:+0.000;-0.000} (x {edid.Primaries.White.X:0.0000}, y {edid.Primaries.White.Y:0.0000})"), Confidence.ManufacturerData);
            Row(html, "Gamut", F($"{edid.SrgbAreaRatio:P0} of the sRGB area"), Confidence.ManufacturerData);
        }

        html.Append("</table>");
        if (report.Channels.GrayLevels is { Count: > 0 } levels)
        {
            html.Append("<h2>Gray balance</h2><table><tr><th>Gray</th><th>Share of white</th><th>Red, green, blue vs their average</th><th></th></tr>");
            foreach (var level in levels)
            {
                html.Append(F($"<tr><td>code {level.Code:0}</td><td>{level.Fraction:P1}</td><td>{E(Balance(level.Balance, level.Tint, level.Uncertainty <= 0.03))}</td><td class=\"conf\">± {Percent(level.Uncertainty)}</td></tr>"));
            }

            html.Append("</table><p class=\"conf\">Each channel's light at that gray relative to its own white: equal values mean the gray has white's tint. The direction of a tint is exact; its size depends a little on the camera's color matrix.</p>");
        }

        html.Append(F($"<p class=\"conf\">Confidence: High = null measurement (the camera's response cancels); Low = assumes a typical camera response; EDID = the manufacturer's data, not measured. Display key {E(report.DisplayKey ?? "unknown")}.</p>"));
        html.Append("</body></html>");
        return html.ToString();
    }

    /// <summary>"R +2.0 %, G +1.9 %, B −3.9 % — yellowish", "… — neutral", or "… — too noisy to judge".</summary>
    private static string Balance(Vec3 balance, string? tint, bool judged)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"R {(balance.X - 1) * 100:+0.0;−0.0;0.0} %, G {(balance.Y - 1) * 100:+0.0;−0.0;0.0} %, B {(balance.Z - 1) * 100:+0.0;−0.0;0.0} %");
        return text + " — " + (tint ?? (judged ? "neutral" : "too noisy to judge"));
    }

    private static string Percent(double fraction) =>
        fraction < 0.0005 ? "<0.1 %" : string.Create(CultureInfo.InvariantCulture, $"{fraction:P1}");

    private static void Row(StringBuilder html, string label, string value, Confidence confidence)
    {
        var tag = confidence == Confidence.ManufacturerData ? "EDID" : confidence.ToString();
        html.Append(CultureInfo.InvariantCulture, $"<tr><th>{WebUtility.HtmlEncode(label)}</th><td>{WebUtility.HtmlEncode(value)}</td><td class=\"conf\">{tag}</td></tr>");
    }

    /// <summary>Measured points against the target power law, on linear axes (code → fraction of white).</summary>
    private static string ToneSvg(ToneSummary tone, double targetGamma)
    {
        const double W = 640, H = 360, L = 48, R = 16, T = 12, B = 36;
        string X(double code) => (L + (code / 255 * (W - L - R))).ToString("0.0", CultureInfo.InvariantCulture);
        string Y(double fraction) => (T + ((1 - fraction) * (H - T - B))).ToString("0.0", CultureInfo.InvariantCulture);
        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"Tone curve\">");
        for (var i = 0; i <= 4; i++)
        {
            var f = i / 4.0;
            svg.Append(CultureInfo.InvariantCulture, $"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{Y(f)}\" y2=\"{Y(f)}\" stroke=\"var(--line)\"/>");
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{L - 6}\" y=\"{Y(f)}\" font-size=\"11\" text-anchor=\"end\" dominant-baseline=\"middle\" fill=\"var(--muted)\">{f:0.##}</text>");
            svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{X(255 * f)}\" y=\"{H - 14}\" font-size=\"11\" text-anchor=\"middle\" fill=\"var(--muted)\">{255 * f:0}</text>");
        }

        var target = string.Join(' ', Enumerable.Range(0, 52).Select(i => $"{X(i * 5)},{Y(Math.Pow(i * 5 / 255.0, targetGamma))}"));
        svg.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{target}\" fill=\"none\" stroke=\"var(--muted)\" stroke-dasharray=\"4 4\"/>");
        var measured = string.Join(' ', new[] { new CurvePoint(0, 0) }.Concat(tone.Points).Append(new CurvePoint(255, 1)).Select(p => $"{X(p.Code)},{Y(p.Fraction)}"));
        svg.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{measured}\" fill=\"none\" stroke=\"var(--accent)\" stroke-width=\"2\"/>");
        foreach (var p in tone.Points)
        {
            svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{X(p.Code)}\" cy=\"{Y(p.Fraction)}\" r=\"3.5\" fill=\"var(--accent)\"/>");
        }

        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"{W - R}\" y=\"{T + 12}\" font-size=\"12\" text-anchor=\"end\" fill=\"var(--muted)\">dashed: gamma {targetGamma:0.0}; dots: measured</text></svg>");
        return svg.ToString();
    }
}
