using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using WaitView = FeatureFlow.Api.Contracts.WaitView;
using WaitKind = FeatureFlow.Api.Contracts.WaitKind;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    sealed partial class Handlers
    {
        /// <summary>
        /// A run's waits as the caller sees them, in the order given: each with whether the caller may answer it now
        /// (<see cref="MayAnswerAsync"/>), who has answered while more must (<c>min_count</c>), and once closed the answer that
        /// closed it. <paramref name="answers"/> is the run's answers, or null to read them only if a wait needs them.
        /// </summary>
        async Task<IReadOnlyList<WaitView>> WaitViewsAsync(Caller caller, RunRecord run, IReadOnlyList<WaitRecord> waits, IReadOnlyList<AnswerRecord>? answers,
            CancellationToken ct)
        {
            var reads = new WaitReads(this, run, answers, ct);
            var views = new List<WaitView>();
            foreach (var wait in waits)
            {
                var (canAnswer, minCount) = await MayAnswerAsync(caller, run, wait, ct);
                var view = WaitViewOf(wait, canAnswer, minCount,
                    wait.Open && minCount > 1 ? ProgressOf(wait, await reads.AnswersAsync(), minCount) : null,
                    wait.Open ? null : ClosedAnswerOf(wait, await reads.AnswersAsync()));
                views.Add(await reads.DescribeAsync(view, wait));
            }

            return views;
        }

        /// <summary>
        /// What a wait's view shows beyond its record (A5, A7, A11, A15; design-v2 s4.5), from the run's other records, each read
        /// once and only when a wait needs it: its title and one-line summary; for an intervention, its cause with the attempts
        /// people granted the step; for a patch approval, the patch; for a merge, the pull request; for a gate, the session on it;
        /// and on an open gate, each consumed artifact's type and <c>changes</c>.
        /// </summary>
        sealed class WaitReads(Handlers api, RunRecord run, IReadOnlyList<AnswerRecord>? answers, CancellationToken ct)
        {
            IReadOnlyList<OverrideRecord>? overrides;
            IReadOnlyList<WaitRecord>? waits;
            IReadOnlyList<PullRequestRecord>? pullRequests;
            IReadOnlyList<SessionRecord>? sessions;
            IReadOnlyList<ArtifactRecord>? artifacts;
            int? needPerson;
            readonly Dictionary<string, IReadOnlyList<string>?> changes = new(StringComparer.Ordinal);

            async Task<IReadOnlyList<T>> QueryAsync<T>(string query, string runId) =>
                (await api.Records.QueryAsync<T>(query, new Dictionary<string, string> { ["run_id"] = runId }, ct)).Unwrap();

            public async Task<IReadOnlyList<AnswerRecord>> AnswersAsync() => answers ??= await QueryAsync<AnswerRecord>(Queries.Answers, run.RunId);

            public async Task<WaitView> DescribeAsync(WaitView view, WaitRecord wait)
            {
                var cause = wait.Cause is { } c ? CauseViewOf(c, await GrantsAsync(c.StepId)) : null;
                var pullRequest = wait.DecisionType.EndsWith(".merge_action", StringComparison.Ordinal) ? await PullRequestAsync(wait.StepId) : null;
                var session = wait.Kind == "gate" ? (sessions ??= await QueryAsync<SessionRecord>(Queries.Sessions, run.RunId))
                    .Where(s => s.WaitId == wait.WaitId).OrderBy(s => s.Status == SessionStatuses.Prepared).ThenBy(s => s.At).LastOrDefault()?.SessionId : null;
                var triage = wait is { Open: true, Kind: "gate", DecisionType: FindingsTriage } ? await NeedPersonAsync() : (int?)null;
                var consumed = view.Consumed;
                if (wait is { Open: true, Kind: "gate" })
                {
                    var typed = new List<ConsumedArtifactView>();
                    foreach (var a in consumed)
                        typed.Add(await TypedAsync(a));
                    consumed = typed;
                }

                return view with
                {
                    Consumed = consumed,
                    Title = WaitTitleOf(wait, run, pullRequest, triage, session is not null),
                    Summary = WaitSummaryOf(wait),
                    Cause = cause,
                    Patch = wait.Patch is { } p ? PatchViewOf(p) : null,
                    PullRequest = pullRequest,
                    SessionId = session,
                };
            }

            /// <summary>
            /// The attempts people granted the step, oldest first: an intervention answered with <c>grant_attempts</c> for it, and a
            /// <c>grant_attempts</c> override (its <c>_beyond</c> variant included) on it, each once the run replied to it.
            /// </summary>
            async Task<IReadOnlyList<GrantView>> GrantsAsync(string stepId)
            {
                waits ??= await QueryAsync<WaitRecord>(Queries.Waits, run.RunId);
                overrides ??= await QueryAsync<OverrideRecord>(Queries.Overrides, run.RunId);
                var interventions = waits.Where(w => w.Kind == "intervention").GroupBy(w => w.WaitId).ToDictionary(g => g.Key, g => g.Last().StepId, StringComparer.Ordinal);
                var answered = (await AnswersAsync()).Where(a => Delivered(a.Result) && interventions.ContainsKey(a.WaitId))
                    .Select(a => (Record: a, Args: ArgsOf(a.Answer)))
                    .Where(a => a.Args is { Action: Overrides.GrantAttempts } args && (args.StepId ?? interventions[a.Record.WaitId]) == stepId)
                    .Select(a => new GrantView(a.Args!.Attempts, a.Args.Actor, a.Record.Reason ?? a.Args.Reason, a.Record.At));
                var overridden = overrides.GroupBy(o => o.OverrideId).Select(g => g.Last())
                    .Where(o => o.Key is Overrides.GrantAttempts or Overrides.GrantAttemptsBeyond && o.StepId == stepId && Delivered(o.Result)
                                && long.TryParse(o.Change.GetValueOrDefault("attempts"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
                    .Select(o => new GrantView(long.Parse(o.Change["attempts"], System.Globalization.CultureInfo.InvariantCulture), o.Actor, o.Reason, o.At));
                return answered.Concat(overridden).OrderBy(g => g.At).ToList();
            }

            /// <summary>The pull request a merge waits on: the one its step last read, else the run's only one.</summary>
            async Task<PullRequestView?> PullRequestAsync(string stepId)
            {
                pullRequests ??= await QueryAsync<PullRequestRecord>(Queries.PullRequests, run.RunId);
                var chosen = pullRequests.Where(r => r.StepId == stepId).MaxBy(r => r.ReadAt) ?? (pullRequests.Count == 1 ? pullRequests[0] : null);
                return chosen is null ? null : PullRequestViewOf(chosen);
            }

            /// <summary>How many of the run's own findings need a person (<see cref="FindingsLedger.NeedsPerson"/>), in the classes its plan's person triage decides.</summary>
            async Task<int> NeedPersonAsync()
            {
                if (needPerson is { } known)
                    return known;
                var people = PeopleOf(await AnswersAsync());
                var classes = PersonClassesOf((await api.LoadPlanAsync(run, null, null, ct)).Plan?.Plan);
                var count = 0;
                foreach (var f in (await QueryAsync<FindingRecord>(Queries.Findings, run.RunId)).Where(f => f.RunId == run.RunId && FindingsLedger.AgentMayNotClear(f, classes)))
                    if (FindingsLedger.NeedsPerson(f, classes, (await api.Records.QueryAsync<FindingEventRecord>(Queries.FindingEvents,
                            new Dictionary<string, string> { ["run_id"] = f.RunId, ["finding_id"] = f.FindingId }, ct)).Unwrap(), people))
                        count++;
                return (needPerson = count).Value;
            }

            /// <summary>A consumed artifact with the type the run (or its parent) indexed it under, and its document's <c>changes</c> (LOG 508).</summary>
            async Task<ConsumedArtifactView> TypedAsync(ConsumedArtifactView consumed)
            {
                if (consumed.Ref is not { } reference)
                    return consumed;
                artifacts ??= [.. await QueryAsync<ArtifactRecord>(Queries.Artifacts, run.RunId),
                    .. run.ParentRunId is { } parent ? await QueryAsync<ArtifactRecord>(Queries.Artifacts, parent) : []];
                if (artifacts.LastOrDefault(a => a.Ref == reference && a.Type is not null)?.Type is not { } type)
                    return consumed;
                if (!changes.TryGetValue(reference, out var listed))
                    changes[reference] = listed = await ChangesAsync(reference, type);
                return consumed with { Type = type, Changes = listed };
            }

            async Task<IReadOnlyList<string>?> ChangesAsync(string reference, string type)
            {
                if (await api.FormatOfAsync(run, type, ct) is not { } format)
                    return null;
                var read = await api.Artifacts.GetAsync(new ArtifactRef(reference), ct);
                if (!read.Ok)
                    return null;
                using var bytes = new MemoryStream();
                await using (var stream = read.Value!)
                    await stream.CopyToAsync(bytes, ct);
                return TextOf(bytes.ToArray()) is { } text ? ChangesOf(DocumentOf(text, format)) : null;
            }
        }

        /// <summary>
        /// Who has answered an open wait each way while it needs <paramref name="needed"/> people (LOG 221): its pending answers,
        /// grouped as <see cref="SameWay"/> counts them, each person once.
        /// </summary>
        static IReadOnlyList<AnswerProgress> ProgressOf(WaitRecord w, IReadOnlyList<AnswerRecord> answers, int needed)
        {
            var ways = new List<(AnswerArgs Way, List<string> Actors)>();
            foreach (var a in answers.Where(a => a.WaitId == w.WaitId && a.Result.StartsWith(Pending, StringComparison.Ordinal)))
            {
                if (ArgsOf(a.Answer) is not { } args)
                    continue;
                if (ways.FindIndex(g => SameWay(a.Answer, g.Way)) is var i and >= 0)
                {
                    if (!ways[i].Actors.Contains(a.Actor, StringComparer.Ordinal))
                        ways[i].Actors.Add(a.Actor);
                }
                else
                {
                    ways.Add((args, [a.Actor]));
                }
            }

            return ways.Select(g => new AnswerProgress(ChoiceOf(g.Way), g.Actors, needed)).ToList();
        }
    }

    /// <summary>
    /// A wait record as the API shows it, with whether the caller may answer it now, who has answered while more must
    /// (<c>min_count</c>), and the answer that closed it.
    /// </summary>
    static WaitView WaitViewOf(WaitRecord w, bool canAnswer, int minCount, IReadOnlyList<AnswerProgress>? progress, ClosedAnswer? answer) =>
        new(w.WaitId, KindOf(w.Kind), w.StepId, w.DecisionType, w.Prompt, w.Choices, w.ReasonRequired ?? [], w.Open, w.UpdatedAt,
            (w.Consumed ?? []).Select(c => new ConsumedArtifactView(c.Key, c.Name, c.Ref)).ToList(), canAnswer, minCount, progress, answer);

    /// <summary>The answer that closed the wait: the last one the run replied to, or null if it closed otherwise (a timeout, steering).</summary>
    static ClosedAnswer? ClosedAnswerOf(WaitRecord w, IReadOnlyList<AnswerRecord> answers) =>
        answers.LastOrDefault(a => a.WaitId == w.WaitId && Delivered(a.Result)) is { } a && ArgsOf(a.Answer) is { } args
            ? new ClosedAnswer(args.Actor, ChoiceOf(args), a.Reason ?? args.Reason, a.At)
            : null;

    /// <summary>
    /// Why an intervention stopped its step, as the API shows it, with the attempts people granted the step; it is likely to
    /// repeat when the last two times the step was sent back said the same.
    /// </summary>
    static WaitCauseView CauseViewOf(WaitCause c, IReadOnlyList<GrantView> grants) =>
        new(c.StepId, c.Outcome, c.Reason, c.Said, c.AttemptsUsed, c.AttemptsAllowed,
            c.SentBack.Select(s => new SentBackView(s.Execution, s.ByStep, s.Outcome, s.Text)).ToList(), grants,
            c.SentBack.Count >= 2 && c.SentBack[^1].Text == c.SentBack[^2].Text);

    /// <summary>The patch a patch approval waits on, as the API shows it.</summary>
    static PatchView PatchViewOf(WaitPatch p) =>
        new(p.ProposerType, p.ProposerStepId, p.Reason, p.NeedsApproval, p.ResumeAt, p.BaseRevision, p.FromChild, p.Document);

    /// <summary>A pull request as the run last read it, as the API shows it: files and checks empty where that read had none.</summary>
    static PullRequestView PullRequestViewOf(PullRequestRecord r)
    {
        var s = r.State;
        return new PullRequestView(r.Repository, r.Number, s.Url, s.Title, s.State, s.Draft, s.Mergeable, s.MergeableState, s.Head, s.HeadRevision, s.Base,
            s.Additions, s.Deletions, s.ChangedFiles, (r.Files ?? []).Select(f => new PullRequestFileView(f.Path, f.Status, f.Additions, f.Deletions)).ToList(),
            (r.Checks?.Checks ?? []).Select(c => new CheckView(c.Name, c.State, c.Conclusion, c.Url)).ToList(), s.ApprovedBy, s.ChangesRequestedBy, r.StepId, r.ReadAt);
    }

    /// <summary>A pull request as a link: where, its URL and state.</summary>
    static PullRequestLink PullRequestLinkOf(PullRequestRecord r) => new(r.Repository, r.Number, r.State.Url, r.State.State);

    /// <summary>The decision type of std's person triage of findings.</summary>
    const string FindingsTriage = "findings.triage";

    /// <summary>
    /// A wait's title (design-v2 s4.5): by kind, then for a gate by what it decides (a session, a triage of findings, a spec
    /// approval, a merge), else the prompt's heading. <paramref name="triage"/> is how many findings need a person, where known.
    /// </summary>
    static string WaitTitleOf(WaitRecord w, RunRecord run, PullRequestView? pullRequest, int? triage, bool session)
    {
        var step = w.StepId;
        return w.Kind switch
        {
            "intervention" when w.Cause?.Outcome == Actions.AttemptsExhausted || (w.Cause is null && w.Prompt.StartsWith("attempts exhausted", StringComparison.Ordinal))
                => $"{step} has used all its attempts",
            "intervention" => $"{step} needs you: {(w.Cause?.Outcome is { } outcome ? outcome.Replace('_', ' ') : Bounded(FirstLine(w.Prompt) ?? "an intervention", 80))}",
            "patch_approval" => $"{w.Patch?.ProposerStepId ?? step} proposes a plan change",
            "breakpoint" => $"Paused before {step}",
            "question" => $"{step} asks",
            "stalled" => $"{step} has stalled",
            "stuck" => $"{step} is stuck",
            _ when session => $"Your planning session for {IssueOf(run) ?? run.RunId}",
            _ when w.DecisionType == FindingsTriage => triage is { } n
                ? $"Triage {n} finding{(n == 1 ? "" : "s")} the triager could not clear"
                : "Triage the findings the triager could not clear",
            _ when w.DecisionType.EndsWith(".spec_approval", StringComparison.Ordinal) && IssueOf(run) is { } issue => $"Approve the plan for {issue}",
            _ when w.DecisionType.EndsWith(".merge_action", StringComparison.Ordinal) && pullRequest is { } pr => $"Merge {pr.Repository}#{pr.Number} into {pr.Base}?",
            _ => HeadingOf(w.Prompt) ?? $"{step} waits for your decision",
        };
    }

    /// <summary>
    /// A wait's one-line summary: for an intervention, what last sent its step back, else what the step said, else the
    /// engine's reason; for a patch approval, the proposer's reason; otherwise the prompt's first line that is not a heading.
    /// </summary>
    static string? WaitSummaryOf(WaitRecord w) => w switch
    {
        { Cause: { } c } => FirstLine(c.SentBack.LastOrDefault()?.Text ?? c.Said ?? c.Reason),
        { Patch.Reason: { } reason } => FirstLine(reason),
        _ => FirstLine(w.Prompt),
    } is { } line ? Bounded(line, 200) : null;

    /// <summary>The run's tracker issue with its title where read (<c>ACME-12 Add widgets</c>), or null.</summary>
    static string? IssueOf(RunRecord run) => run.TrackerIssue is { } key ? run.TrackerTitle is { } title ? $"{key} {title}" : key : null;

    /// <summary>A text's first heading, when its first line is one (<c># Approve the plan</c>), without its marks.</summary>
    static string? HeadingOf(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is ['#', ..] heading && heading.TrimStart('#').Trim() is { Length: > 0 } title ? title : null;

    /// <summary>A text's first line that is neither empty nor a heading, trimmed; null when it has none.</summary>
    static string? FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));

    static string Bounded(string text, int length) => text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";

    /// <summary>A wait record's kind as the API names it: the record store's name (<c>patch_approval</c>) read as <see cref="WaitKind"/>.</summary>
    static WaitKind KindOf(string kind) => kind switch
    {
        "gate" => WaitKind.Gate,
        "question" => WaitKind.Question,
        "intervention" => WaitKind.Intervention,
        "patch_approval" => WaitKind.PatchApproval,
        "breakpoint" => WaitKind.Breakpoint,
        "stalled" => WaitKind.Stalled,
        "stuck" => WaitKind.Stuck,
        _ => throw new InvalidOperationException($"a wait record has kind '{kind}', which the API does not know"),
    };
}
