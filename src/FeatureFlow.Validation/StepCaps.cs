using System.Globalization;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Validation;

/// <summary>
/// The organisation's caps on what one step asks for (docs/05 section 3.1; check 22): the per-step attempts ceiling, which
/// also bounds <c>scm.merge</c>'s <c>limits.races</c> (LOG 340), and the <c>merge.require_checks</c> and
/// <c>merge.require_tested</c> floors. Run start applies it to every step and a patch to the steps it adds or changes, so
/// the two paths cannot disagree (F1).
/// </summary>
public sealed record StepCaps(long? AttemptsPerStep, Cap<bool>? RequireChecks, Cap<bool>? RequireTested)
{
    public static StepCaps Of(PolicyConfig policy) => new(policy.AttemptsPerStep, policy.RequireChecks, policy.RequireTested);

    /// <summary>The merge floors, by <c>scm.merge</c> input and the policy key that locks it.</summary>
    IEnumerable<(string Input, string Key, Cap<bool>? Cap)> Floors =>
        [("require_checks", "policy.merge.require_checks", RequireChecks), ("require_tested", "policy.merge.require_tested", RequireTested)];

    /// <summary>
    /// How <paramref name="step"/> asks beyond a locked cap, one message each; <paramref name="attempts"/> is its attempt
    /// allowance. A locked floor admits only its own value, written as a literal, or the input left unset (its default is
    /// on): an expression cannot be shown at run start to give that value, so it is refused (LOG 347).
    /// </summary>
    public IEnumerable<string> Breaches(Step step, long attempts)
    {
        if (AttemptsPerStep is { } ceiling)
        {
            if (step.ConsumesAttempts && attempts > ceiling)
                yield return $"{step.Id} allows {N(attempts)} attempts, above policy.attempts.ceiling.per_step {N(ceiling)} (F4)";
            if (step.Action == "scm.merge" && RacesAsked(step) is { } races && races > ceiling)
                yield return $"{step.Id} allows {N(races)} races, above policy.attempts.ceiling.per_step {N(ceiling)} (F4)";
        }

        if (step.Action != "scm.merge")
            yield break;
        foreach (var (input, key, cap) in Floors)
            if (cap is { Value: true, Locked: true } && step.With?[input] is { } value && (value as YScalar)?.AsBool() != true)
                yield return (value as YScalar)?.AsBool() == false
                    ? $"{step.Id} sets {input} false; {key} is locked true"
                    : $"{step.Id} sets {input} to {ReadableForm.Describe(value, step.Scope)}; {key} is locked true, so {input} must be true or unset";
    }

    /// <summary>The race allowance a step asks for: its own <c>limits.races</c>, else its document's <c>defaults.limits</c>; null for neither.</summary>
    public static long? RacesAsked(Step step) =>
        step.Limits?.Races ?? step.Scope.DocumentScope.Document.Defaults.Limits?.Races;

    /// <summary>
    /// A step's race allowance: what it asks for, else the installation's default held to the per-step ceiling, so the
    /// ceiling bounds races on every path without refusing a step that asks for none (LOG 348).
    /// </summary>
    public static long RaceAllowance(Step step, long defaultRaces, long? ceiling) =>
        RacesAsked(step) ?? (ceiling is { } c ? Math.Min(defaultRaces, c) : defaultRaces);

    static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
}
