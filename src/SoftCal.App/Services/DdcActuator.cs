using System.Globalization;
using SoftCal.Core.Advisor;
using SoftCal.Windows.Ddc;

namespace SoftCal.App.Services;

/// <summary>What the user answered to a change: the value now set (null = stop), or "set it for me".</summary>
internal sealed record PromptAnswer(int? Value, bool SetForMe = false);

/// <summary>The person at the monitor: shown each change, they make it in the menu (or ask SoftCal to, where it can).</summary>
internal interface IAdvicePrompt
{
    Task<PromptAnswer> AskChangeAsync(OsdControl control, int value, Advice advice, bool canSetForMe, CancellationToken cancellationToken);

    Task<bool> ConfirmAsync(Advice advice, CancellationToken cancellationToken);

    /// <summary>A red/green/blue step: the advice, with boxes for the three values (filled with <paramref name="values"/>)
    /// when <paramref name="showValues"/>.</summary>
    Task<GainsAnswer> AskGainsAsync(Advice advice, RgbGains? values, bool showValues, CancellationToken cancellationToken);
}

/// <summary>Every change is made by the user in the monitor's menu.</summary>
internal sealed class PromptActuator(IAdvicePrompt prompt) : IOsdActuator
{
    public async Task<int?> ApplyAsync(OsdControl control, int value, Advice advice, CancellationToken cancellationToken) =>
        (await prompt.AskChangeAsync(control, value, advice, canSetForMe: false, cancellationToken).ConfigureAwait(false)).Value;

    public Task<bool> ConfirmAsync(Advice advice, CancellationToken cancellationToken) => prompt.ConfirmAsync(advice, cancellationToken);
}

/// <summary>
/// Brightness (VCP 0x10) and Contrast (0x12) can be written over DDC/CI through <paramref name="write"/>: always when
/// <paramref name="automatic"/>, otherwise when the user clicks "Set it for me" on that change. Everything else (gamma
/// presets, shadow boosts: no standard VCP code) is made by the user. On a real monitor <paramref name="write"/> is the
/// journaled guard's: written values are kept when the run finishes and put back if it stops (or by the guardian after a
/// crash). The simulator's demo monitor takes every write.
/// </summary>
internal sealed class DdcActuator(
    Func<byte, uint, CancellationToken, Task<DdcWriteOutcome>> write,
    IAdvicePrompt prompt,
    IProgress<string> progress,
    bool automatic) : IOsdActuator
{
    /// <summary>Monitors fade some changes in; readings start after this.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    private static readonly Dictionary<OsdControlId, byte> Codes = new()
    {
        [OsdControlId.Brightness] = 0x10,
        [OsdControlId.Contrast] = 0x12,
    };

    public async Task<int?> ApplyAsync(OsdControl control, int value, Advice advice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (!Codes.TryGetValue(control.Id, out var code))
        {
            return (await prompt.AskChangeAsync(control, value, advice, canSetForMe: false, cancellationToken).ConfigureAwait(false)).Value;
        }

        if (!automatic)
        {
            var answer = await prompt.AskChangeAsync(control, value, advice, canSetForMe: true, cancellationToken).ConfigureAwait(false);
            if (!answer.SetForMe)
            {
                return answer.Value;
            }
        }

        if (await WriteAsync(control, code, value, cancellationToken).ConfigureAwait(false))
        {
            return value;
        }

        return (await prompt.AskChangeAsync(control, value, advice, canSetForMe: false, cancellationToken).ConfigureAwait(false)).Value;
    }

    public Task<bool> ConfirmAsync(Advice advice, CancellationToken cancellationToken) => prompt.ConfirmAsync(advice, cancellationToken);

    private async Task<bool> WriteAsync(OsdControl control, byte code, int value, CancellationToken cancellationToken)
    {
        var outcome = await write(code, (uint)value, cancellationToken).ConfigureAwait(false);
        if (outcome != DdcWriteOutcome.Applied)
        {
            progress.Report($"DDC/CI could not set {control.Name} ({outcome}); please set it in the monitor's menu.");
            return false;
        }

        progress.Report(string.Create(CultureInfo.InvariantCulture, $"Set {control.Name} to {value} over DDC/CI (written to the monitor; put back if the run stops)."));
        await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
