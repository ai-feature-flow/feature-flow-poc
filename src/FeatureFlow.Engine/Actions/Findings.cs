using System.Globalization;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>
/// <c>findings.record</c> and <c>findings.triage</c> (docs/04 section 11): the ledger is the engine's, what to do about
/// findings is the workflow's (RW4). Each returns the <c>findings</c> namespace as it left the ledger, which the workflow
/// gives the core before the outcome, so the step's routes read the new counts (docs/03 section 5.3).
/// </summary>
/// <remarks>
/// Retry-safe: every change is one record batch, which the record store applies whole, and every finding event carries
/// the step's idempotency key. A retried action finds its own event on a finding and counts what that event did instead
/// of applying it again.
/// </remarks>
static class FindingsActions
{
    public const string CodeChange = "code_change";
    public const string Always = "always";
    public const string Never = "never";

    // ---- findings.record ----

    public static async Task<ActionResult> RecordAsync(ActionRun run)
    {
        var source = run.String("source") is { Length: > 0 } s ? s : throw ActionStop.CannotAnswer("findings.record names no source");
        var reopenOn = run.String("reopen_on") ?? CodeChange;

        string reportRef, raisedBy;
        if (Artifact(run, source) is { } entry)
            (reportRef, raisedBy) = (entry.Ref, entry.StepId);
        else if (run.Instance<IFindingsSourceProvider>("findings_source", source) is { } provider)
        {
            var collected = await CollectAsync(run, provider);
            if (collected.Result is { } waitingOrUnavailable)
                return waitingOrUnavailable;
            (reportRef, raisedBy) = (collected.Report!, source);
        }
        else
            throw ActionStop.CannotAnswer($"findings.record: '{source}' is neither an artifact this run produced nor a findings source provider of this installation");

        var report = ReadReport(await run.ReadTextAsync(reportRef), $"{raisedBy}'s findings report {reportRef}");
        var ledger = await LedgerAsync(run);
        var history = new History(run);
        var code = new CodeAt(run);
        var tally = new Dictionary<string, long>(StringComparer.Ordinal) { ["new"] = 0, ["reraised"] = 0, ["reopened"] = 0, ["repeat_fixes"] = 0 };
        var findings = new List<FindingRecord>();
        var events = new List<FindingEventRecord>();
        var now = DateTimeOffset.UtcNow;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reported in report.Findings.Select(f => f with { LedgerId = null, State = null, Location = f.Location with { Repository = RepositoryNames.As(f.Location.Repository, run.Scope.Repositories) } }))
        {
            var fingerprint = FingerprintOf(reported);
            if (!seen.Add(fingerprint))
            {
                run.Events.Add($"{raisedBy}'s finding \"{reported.Title}\" ({reported.Rule} in {reported.Location.File}) has the fingerprint of an earlier one in the report; recorded once");
                continue;
            }
            var head = await code.HeadAsync(reported.Location.Repository);
            var existing = Match(ledger, fingerprint, run.Call.Step.RunId);
            if (existing is null)
            {
                var id = Fingerprints.LedgerId(run.Call.Step.RunId, fingerprint);
                findings.Add(new FindingRecord(run.Call.Step.RunId, id, fingerprint, reported, raisedBy, null, FindingStates.Open, head, head, 0, 0, 0, false, now));
                events.Add(Event(run, run.Call.Step.RunId, id, FindingEventKinds.Raised, FindingStates.Open, raisedBy, null, head, null, now));
                tally["new"]++;
                continue;
            }

            if (await history.ByThisStepAsync(existing) is { } done)
            {
                // A retry: this step already recorded the finding; count what it did.
                Count(tally, done.Kind, existing);
                continue;
            }

            var changed = await code.ChangedAsync(reported.Location, existing.StateRevision, head);
            var reopen = existing.State is FindingStates.Fixed or FindingStates.Declined
                         && (reopenOn == Always || (reopenOn == CodeChange && changed));
            var after = existing with
            {
                Finding = Merge(existing.Finding, reported),
                Reraises = existing.Reraises + 1,
                ReraisedUnchanged = !changed,
                UpdatedAt = now,
            };
            if (reopen)
                after = after with { State = FindingStates.Open, Reopens = existing.Reopens + 1, RaisedRevision = head, StateRevision = head };
            var kind = reopen ? FindingEventKinds.Reopened : FindingEventKinds.Reraised;
            var reason = reopen
                ? reopenOn == Always ? $"re-raised while {existing.State}; reopen_on: always" : $"re-raised while {existing.State}, and the code changed since"
                : $"re-raised while {existing.State}; the code {(changed ? "changed" : "is unchanged")} since its state last changed";
            findings.Add(after);
            events.Add(Event(run, existing.RunId, existing.FindingId, kind, after.State, raisedBy, reason, head, changed, now));
            Count(tally, kind, after);
        }

        if (findings.Count > 0)
            ActionRun.Must(await run.Provider<IRecordStoreProvider>("record_store").AppendAsync(new RecordBatch(Findings: findings, FindingEvents: events), run.Ct),
                "record the findings");
        run.Events.Add($"findings from {raisedBy}: {tally["new"]} new, {tally["reraised"]} re-raised, {tally["reopened"]} reopened, {tally["repeat_fixes"]} fixed twice before");
        return await ReportAsync(run, "recorded", tally.ToDictionary(kv => kv.Key, kv => (object?)kv.Value), reportRef);
    }

    /// <summary>
    /// A finding's fingerprint (docs/01 section 6). An entry with neither a symbol nor a snippet is placed only by rule and
    /// file, so its title stands in for the snippet: two different problems of one rule in one file stay two (review F4).
    /// </summary>
    internal static string FingerprintOf(ReportedFinding f) =>
        string.IsNullOrWhiteSpace(f.Location.Symbol) && string.IsNullOrWhiteSpace(f.Location.Snippet)
            ? Fingerprints.Of(f with { Location = f.Location with { Snippet = "title: " + f.Title } })
            : Fingerprints.Of(f);

    /// <summary>
    /// A re-raise merged into the finding as recorded: the latest wording, but never weaker. Blocking if either said so,
    /// demonstrated over reasoned, the higher severity, and a proof kept (review F2), so a later reviewer cannot downgrade it.
    /// </summary>
    internal static ReportedFinding Merge(ReportedFinding recorded, ReportedFinding raised)
    {
        static int Rank(string severity) => FindingSeverities.All.ToList().IndexOf(severity) is var i and >= 0 ? i : int.MaxValue;
        var severity = Rank(recorded.Severity) <= Rank(raised.Severity) ? recorded.Severity : raised.Severity;
        return raised with
        {
            Blocking = recorded.Blocking || raised.Blocking,
            Basis = recorded.Basis == FindingBases.Demonstrated || raised.Basis == FindingBases.Demonstrated ? FindingBases.Demonstrated : raised.Basis,
            Severity = severity,
            Proof = raised.Proof ?? recorded.Proof,
            RequiredCorrection = raised.RequiredCorrection ?? recorded.RequiredCorrection,
        };
    }

    static void Count(Dictionary<string, long> tally, string kind, FindingRecord after)
    {
        switch (kind)
        {
            case FindingEventKinds.Raised:
                tally["new"]++;
                break;
            case FindingEventKinds.Reraised:
                tally["reraised"]++;
                break;
            case FindingEventKinds.Reopened:
                tally["reraised"]++;
                tally["reopened"]++;
                if (FindingsLedger.IsRepeatFix(after))
                    tally["repeat_fixes"]++;
                break;
        }
    }

    /// <summary>
    /// The finding a fingerprint matches in the ledger (this run's and its descendants'): this run's own first, else the
    /// first in the ledger's order, so every recorder of the tree picks the same one.
    /// </summary>
    static FindingRecord? Match(IReadOnlyList<FindingRecord> ledger, string fingerprint, string runId) =>
        ledger.FirstOrDefault(f => f.Fingerprint == fingerprint && f.RunId == runId) ?? ledger.FirstOrDefault(f => f.Fingerprint == fingerprint);

    /// <summary>
    /// An external reviewer's report: requested once per execution of the step (its handle is remembered across the
    /// activity's retries), then polled. Pending is a wait, which holds no worker between reads; pending past
    /// <see cref="ActionOptions.ReviewTimeout"/>, or the source's own <c>unavailable</c>, is the <c>unavailable</c> outcome.
    /// </summary>
    static async Task<(ActionResult? Result, string? Report)> CollectAsync(ActionRun run, IFindingsSourceProvider provider)
    {
        var repo = run.Repository();
        var handle = new CollectHandle(await run.RememberAsync("findings_request", async () =>
        {
            var head = ActionRun.Must(await run.Vcs.ResolveAsync(repo, run.WorkReference, run.Ct), "read the work branch")
                       ?? throw ActionStop.CannotAnswer($"{repo.FullName} has no work branch {run.Scope.WorkBranch} to review");
            var target = new FindingsTarget(repo, head, run.PullRequest(repo));
            return ActionRun.Must(await provider.RequestAsync(target, run.Key, run.Ct), "ask for a review").Id;
        }));
        var since = await run.SinceAsync();
        var status = ActionRun.Must(await provider.GetStatusAsync(handle, run.Ct), "read the review's state");
        switch (status.State)
        {
            case CollectStates.Ready:
                return (null, ActionRun.Must(await provider.ReadFindingsAsync(handle, run.Ct), "read the review's findings").Id);
            case CollectStates.Pending when DateTimeOffset.UtcNow - since < run.Options.ReviewTimeout:
                return (ActionResult.Waiting($"the review {handle.Id} is pending", run.Options.PollInterval, run.Events), null);
            case CollectStates.Pending:
                run.Events.Add($"the review {handle.Id} did not finish within {run.Options.ReviewTimeout.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes");
                return (await ReportAsync(run, "unavailable", null, null), null);
            default:
                run.Events.Add($"the review {handle.Id} is unavailable: {status.Reason ?? "no reason given"}");
                return (await ReportAsync(run, "unavailable", null, null), null);
        }
    }

    /// <summary>
    /// A findings report as a reviewer or a source wrote it, with every field the fingerprint and the routes need. One
    /// that does not read stops the step for a person: there is no trustworthy outcome to guess (docs/04 section 1).
    /// </summary>
    internal static FindingsReport ReadReport(string text, string what)
    {
        FindingsReport report;
        try
        {
            report = ArtifactShapes.ReadLibrary<FindingsReport>(text, what);
        }
        catch (FormatException e)
        {
            throw ActionStop.CannotAnswer(e.Message);
        }

        var errors = new List<string>();
        if (report.Schema != 1)
            errors.Add($"schema must be 1, not {report.Schema.ToString(CultureInfo.InvariantCulture)}");
        if (report.Findings is null)
            errors.Add("findings must be a list");
        foreach (var (f, n) in (report.Findings ?? []).Select((f, n) => (f, n)))
        {
            var at = $"findings[{n.ToString(CultureInfo.InvariantCulture)}]";
            if (f is null)
            {
                errors.Add($"{at} is empty");
                continue;
            }

            foreach (var (field, value) in new[] { ("rule", f.Rule), ("title", f.Title), ("description", f.Description) })
                if (string.IsNullOrWhiteSpace(value))
                    errors.Add($"{at}.{field} is required");
            if (!FindingSeverities.All.Contains(f.Severity))
                errors.Add($"{at}.severity must be one of {string.Join(", ", FindingSeverities.All)}");
            if (f.Basis is not (FindingBases.Demonstrated or FindingBases.Reasoned))
                errors.Add($"{at}.basis must be demonstrated or reasoned");
            if (f.Location is null || string.IsNullOrWhiteSpace(f.Location.Repository) || string.IsNullOrWhiteSpace(f.Location.File))
                errors.Add($"{at}.location needs repository and file");
        }

        return errors.Count == 0 ? report : throw ActionStop.CannotAnswer($"{what} is not a findings report: {string.Join("; ", errors.Take(10))}");
    }

    // ---- findings.triage ----

    public static async Task<ActionResult> TriageAsync(ActionRun run)
    {
        var from = run.String("from") is { Length: > 0 } f ? f : throw ActionStop.CannotAnswer("findings.triage names no triage artifact (from)");
        var entry = Artifact(run, from) ?? throw ActionStop.CannotAnswer($"findings.triage: no artifact '{from}' has been produced");
        var ledger = await LedgerAsync(run);
        var byId = ledger.GroupBy(x => x.FindingId).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // No states input allows all four; a list given, even an empty one, allows exactly its states (re-check R3).
        var allowed = run.Inputs.ContainsKey("states") ? run.Strings("states") : FindingStates.All;
        var (triage, problems) = ReadTriage(await run.ReadTextAsync(entry.Ref), $"{entry.StepId}'s {from} {entry.Ref}", byId, allowed);
        var code = new CodeAt(run);
        if (problems.Count == 0)
            foreach (var t in triage!.Entries.Where(t => t.State == FindingStates.Fixed && byId[t.Finding].State != FindingStates.Fixed))
            {
                // A fix needs a commit: nothing on the work branch since the finding was raised or reopened is no fix (re-check R1).
                var raised = byId[t.Finding];
                if (raised.RaisedRevision is not { } at)
                    problems.Add($"{t.Finding}: it was raised in {raised.Finding.Location.Repository}, which this run has not prepared, so no commit of this run can fix it (LOG 190)");
                else if (await code.HeadAsync(raised.Finding.Location.Repository) == at)
                    problems.Add($"{t.Finding}: nothing has been committed to {raised.Finding.Location.Repository} since it was raised at {EngineActions.Short(at)}, so it cannot be fixed");
            }

        if (problems.Count > 0)
        {
            // Nothing is applied: the triage goes back to its producer (std routes invalid there, LOG 161).
            foreach (var problem in problems)
                run.Events.Add($"{entry.StepId}'s {from}: {problem}");
            return await ReportAsync(run, "invalid", null, null);
        }

        var actor = await ActorAsync(run, entry);
        var history = new History(run);
        var findings = new List<FindingRecord>();
        var events = new List<FindingEventRecord>();
        var now = DateTimeOffset.UtcNow;
        var applied = 0;
        foreach (var t in triage!.Entries)
        {
            var before = byId[t.Finding];
            if (await history.ByThisStepAsync(before) is not null)
                continue;
            var head = await code.HeadAsync(before.Finding.Location.Repository);
            if (before.State != t.State)
            {
                findings.Add(before with
                {
                    State = t.State,
                    StateRevision = head ?? before.StateRevision,
                    Fixes = before.Fixes + (t.State == FindingStates.Fixed ? 1 : 0),
                    UpdatedAt = now,
                });
                applied++;
            }

            events.Add(Event(run, before.RunId, before.FindingId, FindingEventKinds.Triaged, t.State, actor, t.Reason, head, null, now));
        }

        if (events.Count > 0)
            ActionRun.Must(await run.Provider<IRecordStoreProvider>("record_store").AppendAsync(new RecordBatch(Findings: findings, FindingEvents: events), run.Ct),
                "apply the triage");
        run.Events.Add($"triage from {entry.StepId}" + (actor == entry.StepId ? "" : $", by {actor}") + $": {triage.Entries.Count} entries, {applied} state changes");

        if (run.Bool("post_declines", true))
            foreach (var t in triage.Entries.Where(t => t.State == FindingStates.Declined))
                await PostDeclineAsync(run, byId[t.Finding], t);

        // Counted from the triage and the ledger, never from what its producer reports about itself (review F1).
        var outputs = new Dictionary<string, object?>
        {
            ["escalated"] = (long)triage.Entries.Count(t => t.State == FindingStates.Escalated),
            ["declined_blocking"] = (long)triage.Entries.Count(t => t.State == FindingStates.Declined
                && (byId[t.Finding].Finding.Blocking || byId[t.Finding].Finding.Basis == FindingBases.Demonstrated)),
        };
        return await ReportAsync(run, "applied", outputs, null);
    }

    /// <summary>
    /// Who a triage's state changes are recorded as (F8; LOG 169, 248): the authenticated person whose answer to the decision
    /// step that produced it gave this exact artifact, as the API recorded the answer before delivering it; otherwise the
    /// producing step (an agent's triage is its step's). Only a decision's wait can carry an answer, so an agent step's
    /// artifact never takes a person's name.
    /// </summary>
    static async Task<string> ActorAsync(ActionRun run, ArtifactEntry entry)
    {
        if (entry.Action is not null)
            return entry.StepId;
        var records = run.Provider<IRecordStoreProvider>("record_store");
        var p = new Dictionary<string, string> { ["run_id"] = run.Call.Step.RunId };
        var waits = ActionRun.Must(await records.QueryAsync<WaitRecord>(Queries.Waits, p, run.Ct), "read the run's waits")
            .Where(w => w.Kind == "decision" && w.StepId == entry.StepId).Select(w => w.WaitId).ToHashSet(StringComparer.Ordinal);
        if (waits.Count == 0)
            return entry.StepId;
        var answers = ActionRun.Must(await records.QueryAsync<AnswerRecord>(Queries.Answers, p, run.Ct), "read the run's answers");
        // The API records an answer as sent before delivering it; the run accepted this artifact from one of them.
        return answers.Where(a => waits.Contains(a.WaitId) && a.Result is "sent" or "recorded").Reverse()
            .FirstOrDefault(a => AnswerArtifacts(a.Answer).Contains(entry.Ref))?.Actor ?? entry.StepId;
    }

    static IReadOnlyCollection<string> AnswerArtifacts(string answer)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<FeatureFlow.Interpreter.Wire.AnswerArgs>(answer, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                ?.Artifacts?.Values.ToList() ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The triage, and what is wrong with it against the ledger: a finding the ledger does not hold, one named twice, a
    /// state that is not one of the four or not one this producer may set (<c>states</c>, review F1), an empty reason.
    /// </summary>
    internal static (Triage? Triage, List<string> Problems) ReadTriage(string text, string what, IReadOnlyDictionary<string, FindingRecord> ledger,
        IReadOnlyList<string> allowed)
    {
        Triage triage;
        try
        {
            triage = ArtifactShapes.ReadLibrary<Triage>(text, what);
        }
        catch (FormatException e)
        {
            return (null, [e.Message]);
        }

        var problems = new List<string>();
        if (triage.Schema != 1)
            problems.Add($"schema must be 1, not {triage.Schema.ToString(CultureInfo.InvariantCulture)}");
        if (triage.Entries is null)
            return (null, [.. problems, "entries must be a list"]);
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in triage.Entries)
        {
            if (e?.Finding is not { Length: > 0 } id)
            {
                problems.Add("an entry names no finding");
                continue;
            }

            if (!ledger.ContainsKey(id))
                problems.Add($"{id} is not a finding in this run's ledger");
            else if (!named.Add(id))
                problems.Add($"{id} is named more than once");
            if (!FindingStates.All.Contains(e.State))
                problems.Add($"{id}: state must be one of {string.Join(", ", FindingStates.All)}, not '{e.State}'");
            else if (!allowed.Contains(e.State))
                problems.Add($"{id}: this step may set only {string.Join(", ", allowed)}, not '{e.State}'");
            if (string.IsNullOrWhiteSpace(e.Reason))
                problems.Add($"{id}: a reason is required");
        }

        return (problems.Count == 0 ? triage : null, problems);
    }

    static string DeclineBody(FindingRecord finding, string? reason)
    {
        var where = finding.Finding.Location.Symbol is { Length: > 0 } symbol ? $"`{finding.Finding.Location.File}` ({symbol})" : $"`{finding.Finding.Location.File}`";
        return $"**Finding {finding.FindingId} declined**: {finding.Finding.Title}\n\n{where}\n\n{reason}";
    }

    /// <summary>
    /// The declines the ledger holds for a repository, posted when its pull request opens or is updated (M6 follow-up 5;
    /// LOG 164, 249), unless the step says <c>post_declines: false</c>. Each is posted under the key of the triage that
    /// declined it, the key that triage posts under itself, so a decline is posted once whichever comes first. Only this
    /// run's own findings: a child's belong on its own pull request.
    /// </summary>
    internal static async Task PostLedgerDeclinesAsync(ActionRun run, RepoRef repo, PullRequestRef pr)
    {
        if (!run.Bool("post_declines", true))
            return;
        var records = run.Provider<IRecordStoreProvider>("record_store");
        var runId = run.Call.Step.RunId;
        var declined = ActionRun.Must(await records.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = runId }, run.Ct), "read the findings ledger")
            .Where(f => f.RunId == runId && f.State == FindingStates.Declined && RepositoryNames.As(f.Finding.Location.Repository, [repo.FullName]) == repo.FullName);
        foreach (var finding in declined)
        {
            var history = ActionRun.Must(await records.QueryAsync<FindingEventRecord>(Queries.FindingEvents,
                new Dictionary<string, string> { ["run_id"] = runId, ["finding_id"] = finding.FindingId }, run.Ct), "read the finding's history");
            if (history.LastOrDefault(e => e.Kind == FindingEventKinds.Triaged && e.State == FindingStates.Declined) is not { } decline)
                continue;
            var comment = ActionRun.Must(await run.CodeHost.AddCommentAsync(pr, DeclineBody(finding, decline.Reason), $"{decline.Key}/{finding.FindingId}", run.Ct),
                "post a decline on the pull request");
            run.Events.Add($"the decline of {finding.FindingId} is posted on {ActionRun.Format(pr)} ({comment.Id})");
        }
    }

    /// <summary>
    /// A decline's reason on the pull request of the finding's repository, once per triage and finding (the comment's
    /// key). Before a pull request is open there is nowhere to post it; the timeline says so (LOG 164).
    /// </summary>
    static async Task PostDeclineAsync(ActionRun run, FindingRecord finding, TriageEntry entry)
    {
        var repo = new RepoRef(finding.Finding.Location.Repository);
        if (run.PullRequest(repo) is not { } pr)
        {
            run.Events.Add($"the decline of {finding.FindingId} is not posted: no pull request is open for {repo.FullName}");
            return;
        }

        var comment = ActionRun.Must(await run.CodeHost.AddCommentAsync(pr, DeclineBody(finding, entry.Reason), $"{run.Key}/{finding.FindingId}", run.Ct),
            "post a decline on the pull request");
        run.Events.Add($"the decline of {finding.FindingId} is posted on {ActionRun.Format(pr)} ({comment.Id})");
    }

    // ---- Shared ----

    /// <summary>
    /// The artifact of a name the step reads: the latest one nearest the step's own scope, as the workflow resolves
    /// <c>consumes</c>, so a recorder inside a <c>for_each</c> branch reads its own branch's reviewer's report.
    /// </summary>
    static ArtifactEntry? Artifact(ActionRun run, string name)
    {
        var stepId = run.Call.Step.StepId;
        var prefix = stepId.Contains('.', StringComparison.Ordinal) ? stepId[..(stepId.LastIndexOf('.') + 1)] : "";
        return run.Scope.Artifacts.Where(a => a.Name == name)
            .OrderByDescending(a => a.StepId.StartsWith(prefix, StringComparison.Ordinal))
            .ThenBy(a => a.StepId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    static async Task<IReadOnlyList<FindingRecord>> LedgerAsync(ActionRun run) =>
        ActionRun.Must(await run.Provider<IRecordStoreProvider>("record_store").QueryAsync<FindingRecord>(Queries.Findings,
            new Dictionary<string, string> { ["run_id"] = run.Call.Step.RunId }, run.Ct), "read the findings ledger");

    static FindingEventRecord Event(ActionRun run, string runId, string findingId, string kind, string state, string actor, string? reason, string? revision,
        bool? codeChanged, DateTimeOffset at) =>
        new(runId, findingId, run.Key, kind, state, actor, reason, run.Call.Step.StepId, revision, codeChanged, at);

    /// <summary>
    /// The outcome, with the <c>findings</c> namespace as the ledger now stands and the ledger's version: how many events its
    /// findings hold. Events are only appended, and each write is one batch with an event per finding it changes, so a
    /// later ledger has a higher version, and parallel recorders' counts are applied newest last (review F3).
    /// </summary>
    static async Task<ActionResult> ReportAsync(ActionRun run, string outcome, IReadOnlyDictionary<string, object?>? outputs, string? evidence)
    {
        var history = new History(run);
        var ledger = await LedgerAsync(run);
        long version;
        for (var tries = 0; ; tries++)
        {
            version = 0;
            foreach (var finding in ledger)
                version += (await history.OfAsync(finding)).Count;
            // The counts and the version must describe one ledger: read again until no writer came between.
            var again = await LedgerAsync(run);
            if (again.SequenceEqual(ledger) || tries >= 4)
            {
                ledger = again;
                break;
            }

            ledger = again;
        }

        return run.Report(outcome, outputs, evidence) with { Findings = FindingsLedger.Counts(ledger), FindingsVersion = version };
    }

    /// <summary>A finding's history, read when the step may already have recorded it (a retry).</summary>
    sealed class History(ActionRun run)
    {
        public async Task<IReadOnlyList<FindingEventRecord>> OfAsync(FindingRecord finding) =>
            ActionRun.Must(await run.Provider<IRecordStoreProvider>("record_store").QueryAsync<FindingEventRecord>(Queries.FindingEvents,
                new Dictionary<string, string> { ["run_id"] = finding.RunId, ["finding_id"] = finding.FindingId }, run.Ct), "read a finding's history");

        public async Task<FindingEventRecord?> ByThisStepAsync(FindingRecord finding) => (await OfAsync(finding)).LastOrDefault(e => e.Key == run.Key);
    }

    /// <summary>
    /// The code at a finding: the work branch head per repository of the run, and whether the finding's file differs
    /// between two revisions. File granularity (LOG 162): the fingerprint has no line numbers and a symbol is not parsed.
    /// A revision that is not known, or a repository that is not the run's, counts as changed.
    /// </summary>
    sealed class CodeAt(ActionRun run)
    {
        readonly Dictionary<string, string?> heads = new(StringComparer.Ordinal);
        readonly Dictionary<(string, string, string), string?> files = [];

        public async Task<string?> HeadAsync(string repository)
        {
            if (!run.Scope.Repositories.Contains(repository))
                return null;
            if (!heads.TryGetValue(repository, out var head))
                heads[repository] = head = ActionRun.Must(await run.Vcs.ResolveAsync(new RepoRef(repository), run.WorkReference, run.Ct), "read the work branch");
            return head;
        }

        public async Task<bool> ChangedAsync(FindingLocation location, string? since, string? head)
        {
            if (since is null || head is null || !run.Scope.Repositories.Contains(location.Repository))
                return true;
            if (since == head)
                return false;
            return await FileAsync(location, since) != await FileAsync(location, head);
        }

        async Task<string?> FileAsync(FindingLocation location, string revision)
        {
            var key = (location.Repository, location.File, revision);
            if (!files.TryGetValue(key, out var hash))
                files[key] = hash = ActionRun.Must(await run.CodeHost.ReadFileAsync(new RepoRef(location.Repository), location.File, revision, run.Ct),
                    $"read {location.File} at {EngineActions.Short(revision)}")?.Hash;
            return hash;
        }
    }
}
