using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Validation;

namespace FeatureFlow.Interpreter;

/// <summary>
/// An artifact a step consumes (docs/03 section 7.2): the key it goes by in the step's inputs and sandbox (its name from this
/// run, else with its source in front: <c>parent/contract</c>, <c>previous/requirements</c>, <c>child/ticket_a/summary</c>), its name, whether it is
/// <c>optional</c>, whether it is the run's findings ledger view (staged by the agent worker, never a produced artifact; LOG 63,
/// 167), and its reference now, or null when there is none.
/// </summary>
public sealed record ConsumedArtifact(string Key, string Name, bool Optional, bool LedgerView, string? Reference);

/// <summary>An artifact the run holds, as an engine action sees it (<see cref="ScopeFacts"/>); <see cref="Producer"/> is the engine action that produced it.</summary>
public sealed record HeldArtifact(string StepId, string Name, string? Type, string Reference, string? Producer, bool Evidence);

/// <summary>
/// What an engine action knows of the run beyond its inputs, computed from the plan and state when it starts: the artifacts
/// the run holds, the pull requests its steps opened, the step's repository, the work branch, what it consumes (by key) and
/// declares it consumes, and the gate the step raised in this execution with its state.
/// </summary>
public sealed record ScopeFacts(
    IReadOnlyList<HeldArtifact> Artifacts,
    IReadOnlyList<string> PullRequests,
    string? Repository,
    string WorkBranch,
    IReadOnlyDictionary<string, string> Consumed,
    IReadOnlyList<string> Declared,
    string? GateType,
    string? GateState);

/// <summary>The artifacts a run holds and produces, and what a step consumes of them (docs/03 section 7; check 32).</summary>
public sealed partial class RunCore
{
    /// <summary>Every artifact a step consumes, in the order it declares them, each resolved now.</summary>
    public IReadOnlyList<ConsumedArtifact> Consumed(string stepId)
    {
        var step = StepById(stepId) ?? throw new EvaluationException($"{stepId} is not a step of plan revision {State.Revision}");
        return step.Consumes.Select(c => IsLedgerView(c)
                ? new ConsumedArtifact(c.Name, c.Name, c.Optional, true, null)
                : new ConsumedArtifact(ConsumedKey(c), c.Name, c.Optional, false, ConsumedReference(step, c)))
            .ToList();
    }

    /// <summary>
    /// Check 32 at step start (docs/09 section 2): every artifact the step consumes, and does not mark <c>optional</c>, exists:
    /// from its own run, its parent or root as they were when this run started, or a child as it ended. Why not, or null.
    /// </summary>
    public string? MissingConsumed(string stepId)
    {
        var missing = Consumed(stepId).Where(c => c is { Optional: false, LedgerView: false, Reference: null }).Select(c => c.Key).Distinct().ToList();
        return missing.Count == 0 ? null : $"check 32 ({Checks.Title(Checks.ConsumedArtifacts)}): {stepId} consumes {string.Join(", ", missing)}, which no step has produced";
    }

    /// <summary>
    /// The artifact a step consumes: from this run, the latest of its name nearest the step; from <c>previous</c>, the same
    /// over each step's earlier executions; from the parent or root, as it was when this run started (the root of a root run
    /// is the run itself); from a child, as the child ended (its <c>run.child</c> step holds it). Null when there is none.
    /// </summary>
    string? ConsumedReference(Step step, ArtifactIn consumed)
    {
        const string child = "child:";
        return consumed.From switch
        {
            "current" => LatestArtifact(step, consumed.Name),
            "previous" => LatestArtifact(step, consumed.Name, previous: true),
            "parent" => Run.ParentArtifacts?.GetValueOrDefault(consumed.Name),
            "root" => Run.ParentId is null ? LatestArtifact(step, consumed.Name) : Run.RootArtifacts?.GetValueOrDefault(consumed.Name),
            _ when consumed.From.StartsWith(child, StringComparison.Ordinal)
                   && Plan.FindStep(consumed.From[child.Length..], step.Scope) is { Descriptor.Handling: ActionHandling.ChildRun } starter =>
                State.Steps.GetValueOrDefault(starter.Id)?.Current?.Artifacts.GetValueOrDefault(consumed.Name),
            _ => null,
        };
    }

    static bool IsLedgerView(ArtifactIn consumed) => consumed.From == "current" && consumed.Name == PlanValidator.FindingsLedger;

    static string ConsumedKey(ArtifactIn consumed) =>
        consumed.From == "current" ? consumed.Name : $"{consumed.From.Replace(':', '/')}/{consumed.Name}";

    /// <summary>
    /// The latest artifact of a name in this run, nearest a step's own scope (the run's top level when null). A child's are not
    /// this run's. With <paramref name="previous"/>, the same over each step's earlier executions (<c>from: previous</c>): the
    /// latest of the name from any execution before the step's current one.
    /// </summary>
    string? LatestArtifact(Step? near, string name, bool previous = false)
    {
        var prefix = near?.Scope.Prefix ?? "";
        IReadOnlyDictionary<string, string>? Of(StepRecord record) => previous ? record.EarlierArtifacts : record.Current?.Artifacts;
        return State.Steps
            .Where(kv => Of(kv.Value)?.ContainsKey(name) == true && !HoldsChildArtifacts(kv.Key))
            .OrderByDescending(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => Of(kv.Value)![name])
            .FirstOrDefault();
    }

    bool HoldsChildArtifacts(string stepId) => StepById(stepId)?.Descriptor?.Handling == ActionHandling.ChildRun;

    /// <summary>The latest artifact of every name this run holds, nearest a step: what a child it starts sees as its parent's.</summary>
    SortedDictionary<string, string> RunArtifacts(string nearStepId)
    {
        var near = StepById(nearStepId);
        // A run.child step's artifacts are its child's: LatestArtifact skips them, so a name only a child produced is left out.
        var names = State.Steps.SelectMany(kv => kv.Value.Current?.Artifacts.Keys ?? Enumerable.Empty<string>()).Distinct();
        var artifacts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
            if (LatestArtifact(near, name) is { } reference)
                artifacts[name] = reference;
        return artifacts;
    }

    /// <summary>
    /// The list a <c>plan.expand</c> step reads: the name its <c>from</c> evaluated to when check 31 passed its
    /// <paramref name="inputs"/> at step start (<see cref="StepSpec"/>), and the latest artifact of it nearest the step, or null.
    /// </summary>
    public (string? Name, string? Reference) ExpandList(string stepId, IReadOnlyDictionary<string, object?> inputs)
    {
        var name = inputs.GetValueOrDefault("from") as string;
        return (name, name is null ? null : LatestArtifact(StepById(stepId), name));
    }

    /// <summary>Whether an artifact type is marked <c>evidence: true</c> (F7).</summary>
    bool IsEvidence(ItemRef? type) => type?.Reference is { } typeRef && libraries?.Resolve(typeRef)?.ArtifactType(typeRef.Name)?.Evidence == true;

    /// <summary>Indexes the artifacts a step's execution produced (<see cref="RunState.Produced"/>); a child's are not this run's.</summary>
    void Produce(Step step, StepRecord record, IReadOnlyDictionary<string, string>? artifacts)
    {
        if (artifacts is null || HoldsChildArtifacts(step.Id))
            return;
        foreach (var (name, reference) in artifacts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var type = step.Produces.FirstOrDefault(p => p.Name == name)?.Type;
            State.Produced.Add(new ProducedArtifact(step.Id, record.Current?.Number ?? 0, name, type?.Text, reference,
                step.Form == StepForm.Action ? step.Action : null, IsEvidence(type)));
        }
    }

    /// <summary>
    /// What an engine action at <paramref name="stepId"/> knows of the run (<see cref="ScopeFacts"/>). With <paramref name="submitted"/>,
    /// as it will be once a session submits those files: the session's step holds them, and the step's own artifacts from an
    /// earlier execution are gone, since its start clears them (DR12); a pre-submit check runs on that (docs/09 section 4.2).
    /// </summary>
    public ScopeFacts ScopeFacts(string stepId, (string SessionStepId, IReadOnlyDictionary<string, string> Artifacts)? submitted = null)
    {
        var step = StepById(stepId) ?? throw new EvaluationException($"{stepId} is not a step of plan revision {State.Revision}");
        var artifacts = new List<HeldArtifact>();
        var pullRequests = new List<string>();
        foreach (var other in Plan.AllSteps.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (submitted is { } s && (other.Id == s.SessionStepId || other.Id == step.Id))
                continue;
            if (State.Steps.GetValueOrDefault(other.Id)?.Current is not { } current)
                continue;
            // A run.child step holds its child's artifacts, which are not this run's.
            foreach (var (name, reference) in current.Artifacts.Where(_ => !HoldsChildArtifacts(other.Id)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var type = other.Produces.FirstOrDefault(p => p.Name == name)?.Type;
                artifacts.Add(new HeldArtifact(other.Id, name, type?.Text, reference, other.Form == StepForm.Action ? other.Action : null, IsEvidence(type)));
            }

            if (other.Descriptor?.OpensPullRequest == PullRequestKind.Work && current.Outputs.GetValueOrDefault("pull_request") is string pr && pr.Length > 0)
                pullRequests.Add(pr);
        }

        var consumed = new Dictionary<string, string>(StringComparer.Ordinal);
        var all = Consumed(stepId).Where(c => !c.LedgerView).ToList();
        foreach (var c in all.Where(c => c.Reference is not null))
            consumed.TryAdd(c.Key, c.Reference!);

        if (submitted is { } given)
        {
            var session = StepById(given.SessionStepId)!;
            foreach (var (name, reference) in given.Artifacts)
            {
                var type = session.Produces.FirstOrDefault(p => p.Name == name)?.Type;
                artifacts.Add(new HeldArtifact(session.Id, name, type?.Text, reference, null, IsEvidence(type)));
            }

            artifacts = artifacts.OrderBy(a => a.StepId, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
            foreach (var c in step.Consumes.Where(c => c.From == "current" && !IsLedgerView(c)))
            {
                // As LatestArtifact picks: nearest the step's scope, then by step id; the step's own are cleared at its start.
                var latest = artifacts.Where(a => a.Name == c.Name && !HoldsChildArtifacts(a.StepId))
                    .OrderByDescending(a => a.StepId.StartsWith(step.Scope.Prefix, StringComparison.Ordinal))
                    .ThenBy(a => a.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (latest is not null)
                    consumed[ConsumedKey(c)] = latest.Reference;
                else
                    consumed.Remove(ConsumedKey(c));
            }
        }

        string? repository;
        try
        {
            repository = ResolveRepository(stepId);
        }
        catch (EvaluationException)
        {
            // A run with no repositories: the action says so if it needs one.
            repository = null;
        }

        var gateState = ActionDecisionState(stepId);
        return new ScopeFacts(artifacts, pullRequests, repository, WorkBranch(), consumed, all.Select(c => c.Key).Distinct().ToList(),
            gateState is null ? null : ActionDecisionTypeOf(stepId), gateState);
    }

    /// <summary>
    /// The run's pull requests: the <c>pull_request</c> output of its steps whose action opens one (R1 F7; <c>scm.pull_request</c>
    /// and <c>artifact.publish</c>), current and previous executions. Each opens its pull request keyed by run and repository, so
    /// earlier executions name the same one.
    /// </summary>
    public IReadOnlyList<string> PullRequestsOpened() =>
        State.Steps.Where(kv => StepById(kv.Key)?.Descriptor?.OpensPullRequest is not null).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .SelectMany(kv => new[] { kv.Value.Previous, kv.Value.Current })
            .Select(e => e?.Outputs.GetValueOrDefault("pull_request") as string)
            .OfType<string>().Where(pr => pr.Length > 0).Distinct().ToList();
}
