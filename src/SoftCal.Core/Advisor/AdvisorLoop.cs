namespace SoftCal.Core.Advisor;

/// <summary>Carries out the advice: the user at the monitor, DDC/CI, or a simulated user in tests.</summary>
public interface IOsdActuator
{
    /// <summary>Sets <paramref name="control"/> to <paramref name="value"/> (or asks the user to) and returns the value
    /// now set, as reported; null when it could not be done (the run stops).</summary>
    Task<int?> ApplyAsync(OsdControl control, int value, Advice advice, CancellationToken cancellationToken);

    /// <summary>Asks for a fix outside the monitor's controls; false stops the run.</summary>
    Task<bool> ConfirmAsync(Advice advice, CancellationToken cancellationToken);
}

/// <summary>One step of an advisor run: the advice, the value reported after it, and the readings taken next.</summary>
public sealed record AdvisorStep(Advice Advice, int? ReportedValue, AdvisorReadings Readings);

/// <summary>Runs the advisor: measure, advise, act, measure what the advice says, until it is done.</summary>
public sealed class AdvisorLoop(AdvisorProbe probe, OsdAdvisor advisor, IOsdActuator actuator, MonitorControlProfile profile)
{
    private readonly AdvisorProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    private readonly OsdAdvisor _advisor = advisor ?? throw new ArgumentNullException(nameof(advisor));
    private readonly IOsdActuator _actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
    private readonly MonitorControlProfile _profile = profile ?? throw new ArgumentNullException(nameof(profile));

    /// <param name="settings">The monitor's current settings; updated as values are reported.</param>
    /// <param name="advised">Each advice as it is given (before it is carried out).</param>
    /// <param name="measured">Each finished step with the readings taken after it.</param>
    public async Task<IReadOnlyList<AdvisorStep>> RunAsync(
        IDictionary<OsdControlId, int> settings,
        IProgress<Advice>? advised,
        IProgress<AdvisorStep>? measured,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var readings = await _probe.MeasureAsync(new AdvisorReadings(), ProbeParts.All, cancellationToken).ConfigureAwait(false);
        measured?.Report(new AdvisorStep(new Advice(AdviceKind.Measure, "Start", "First readings."), null, readings));
        var steps = new List<AdvisorStep>();
        while (true)
        {
            var advice = _advisor.Next(settings.AsReadOnly(), readings);
            advised?.Report(advice);
            int? reported = null;
            switch (advice.Kind)
            {
                case AdviceKind.Done:
                    steps.Add(new AdvisorStep(advice, null, readings));
                    return steps;
                case AdviceKind.Fix:
                    if (!await _actuator.ConfirmAsync(advice, cancellationToken).ConfigureAwait(false))
                    {
                        steps.Add(new AdvisorStep(advice, null, readings));
                        return steps;
                    }

                    readings = readings.Stale();
                    break;
                case AdviceKind.Change:
                    var control = _profile.Find(advice.Control!.Value) ?? throw new InvalidOperationException($"The advice names {advice.Control}, which this monitor does not have.");
                    reported = await _actuator.ApplyAsync(control, advice.Value!.Value, advice, cancellationToken).ConfigureAwait(false);
                    if (reported is null)
                    {
                        steps.Add(new AdvisorStep(advice, null, readings));
                        return steps;
                    }

                    settings[control.Id] = control.Clamp(reported.Value);
                    readings = readings.Stale();
                    break;
            }

            readings = await _probe.MeasureAsync(readings, advice.Then, cancellationToken).ConfigureAwait(false);
            var step = new AdvisorStep(advice, reported, readings);
            steps.Add(step);
            measured?.Report(step);
        }
    }
}
