using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter.Wire;
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
            async Task<IReadOnlyList<AnswerRecord>> Answers() =>
                answers ??= (await Records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = run.RunId }, ct)).Unwrap();

            var views = new List<WaitView>();
            foreach (var wait in waits)
            {
                var (canAnswer, minCount) = await MayAnswerAsync(caller, run, wait, ct);
                views.Add(WaitViewOf(wait, canAnswer, minCount,
                    wait.Open && minCount > 1 ? ProgressOf(wait, await Answers(), minCount) : null,
                    wait.Open ? null : ClosedAnswerOf(wait, await Answers())));
            }

            return views;
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
