using System.Text;
using System.Text.Json;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace FeatureFlow.Engine.Agent;

/// <summary>
/// The agent worker's settings: the URLs at which sandboxes reach its replicas (any replica serves any step,
/// RW17), the model proxy's base URL as sandboxes see it, how often commits are detected, and the sandbox client
/// executable copied into each sandbox (LOG 67).
/// </summary>
public sealed record AgentWorkerOptions(
    IReadOnlyList<string> AgentUrls,
    string ModelBaseUrl,
    TimeSpan CommitInterval,
    string? SandboxClient,
    TimeSpan? PollInterval = null);

/// <summary>A work failure of the step: the attempt fails and the core counts it (docs/01 section 4).</summary>
public sealed class StepWorkException(string message) : Exception(message);

/// <summary>The step cannot produce a trustworthy outcome; the run raises an intervention (docs/04 section 1).</summary>
public sealed class StepCannotAnswerException(string message) : Exception(message);

/// <summary>
/// The agent step's activity (docs/04 section 3.1) on an agent worker. It is called by name, <c>RunAgentStep</c>,
/// because <c>Engine</c> may not reference this project (docs/11 section 2.3).
/// <para>
/// Prepare: create the sandbox, or re-attach to it by the step's key; check out the work branch from the hidden
/// reference if an earlier attempt pushed there, else from the branch; create the output folder (DR17), the home
/// directory (DR19) and the inputs manifest. Start the runtime with the step's token, or re-attach to its
/// session. Supervise: heartbeat, and push new commits to the hidden reference about every
/// <see cref="AgentWorkerOptions.CommitInterval"/>. Collect: the report the engine tools recorded, else the file
/// fallback, checked against the contract with correction turns. Finish: commit leftovers, push, move the work
/// branch once, copy the artifacts out, dispose of the sandbox.
/// </para>
/// <para>
/// Everything a later attempt needs is in the record store, the remote, or the sandbox, never in this worker's
/// memory, so a worker killed mid-step loses nothing: Temporal retries the activity and the next worker resumes
/// from the same sandbox (docs/02 section 4.3).
/// </para>
/// </summary>
public sealed class AgentActivities(ProviderSet providers, StepTokens tokens, AgentWorkerOptions options)
{
    readonly StepTokens tokens = tokens;
    readonly AgentWorkerOptions options = options;
    public const string ActivityName = "RunAgentStep";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly SemaphoreSlim clientGate = new(1, 1);
    ArtifactRef? clientArtifact;

    IRecordStoreProvider Records => providers.Required<IRecordStoreProvider>("record_store");

    IArtifactStoreProvider Store => providers.Required<IArtifactStoreProvider>("artifact_store");

    [Activity(ActivityName)]
    public async Task<StepOutcome> RunAgentStepAsync(StepCall call)
    {
        var context = ActivityExecutionContext.Current;
        var runtime = providers.Required<IAgentRuntimeProvider>("agent_runtime", call.DryRun);
        if (call.Agent is null || call.DryRun || providers.ForRun("sandbox", false) is not ISandboxProvider sandboxes
            || providers.ForRun("vcs", false) is not IVcsProvider vcs)
            return await WithoutSandboxAsync(runtime, call, context);
        try
        {
            return await new Run(this, call, runtime, sandboxes, vcs, context).ExecuteAsync();
        }
        catch (StepWorkException e)
        {
            return new StepOutcome(StepOutcome.WorkFailed, null, e.Message);
        }
        catch (StepCannotAnswerException e)
        {
            return new StepOutcome(StepOutcome.CannotAnswer, null, e.Message);
        }
    }

    /// <summary>M3's path, kept for the scripted and recording runtimes: no sandbox, the runtime's own report.</summary>
    static async Task<StepOutcome> WithoutSandboxAsync(IAgentRuntimeProvider runtime, StepCall call, ActivityExecutionContext context)
    {
        var ct = context.CancellationToken;
        var task = new AgentTask(call.Step, call.What, null, call.Inputs, call.Contract);
        var started = await runtime.StartAsync(SandboxHandle.None, task, new AgentLimits(call.Timeout, call.Agent?.CorrectionTurns ?? 2), ct);
        if (Classify(started.Error) is { } early)
            return early;
        var session = started.Value!;
        try
        {
            while (true)
            {
                context.Heartbeat();
                var progress = await runtime.GetProgressAsync(session, ct);
                if (Classify(progress.Error) is { } failed)
                    return failed;
                if (progress.Value!.Finished)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }

            var result = await runtime.GetResultAsync(session, ct);
            return Classify(result.Error) ?? (result.Value!.Report is { } report
                ? new StepOutcome(StepOutcome.Reported, report, null)
                : new StepOutcome(StepOutcome.WorkFailed, null, "the agent ended without reporting"));
        }
        catch (OperationCanceledException)
        {
            await runtime.StopAsync(session, CancellationToken.None);
            throw;
        }
    }

    /// <summary>An infrastructure error is thrown, so Temporal retries and no attempt is used; a work error is returned.</summary>
    static StepOutcome? Classify(ProviderError? error)
    {
        if (error is null)
            return null;
        if (error.Reason == "cannot_answer")
            return new StepOutcome(StepOutcome.CannotAnswer, null, error.Message);
        if (error.Class == ProviderErrorClass.Infrastructure)
            throw new ApplicationFailureException($"{error.Reason}: {error.Message}", errorType: "infrastructure");
        return new StepOutcome(StepOutcome.WorkFailed, null, $"{error.Reason}: {error.Message}");
    }

    /// <summary>The sandbox client, put in the artifact store once per worker, then copied into each sandbox.</summary>
    async Task<ArtifactRef> ClientAsync(CancellationToken ct)
    {
        await clientGate.WaitAsync(ct);
        try
        {
            if (clientArtifact is not null)
                return clientArtifact;
            if (options.SandboxClient is not { } path || !File.Exists(path))
                throw new ApplicationFailureException($"the sandbox client {options.SandboxClient} does not exist: publish FeatureFlow.SandboxClient for the sandbox's OS",
                    errorType: "infrastructure");
            await using var stream = File.OpenRead(path);
            return clientArtifact = Must(await Store.PutAsync(stream, ct));
        }
        finally
        {
            clientGate.Release();
        }
    }

    /// <summary>A provider result's value, or the error thrown by class: infrastructure for Temporal to retry, otherwise a work failure.</summary>
    static T Must<T>(ProviderResult<T> result, string? what = null)
    {
        if (result.Ok)
            return result.Value!;
        var error = result.Error!;
        var message = (what is null ? "" : what + ": ") + $"{error.Reason}: {error.Message}";
        if (error.Reason == "cannot_answer")
            throw new StepCannotAnswerException(message);
        if (error.Class == ProviderErrorClass.Infrastructure)
            throw new ApplicationFailureException(message, errorType: "infrastructure");
        throw new StepWorkException(message);
    }

    /// <summary>One execution of the lifecycle. Its fields are rebuilt from durable state on every activity attempt.</summary>
    sealed class Run(AgentActivities owner, StepCall call, IAgentRuntimeProvider runtime, ISandboxProvider sandboxes, IVcsProvider vcs, ActivityExecutionContext context)
    {
        readonly AgentStepSpec spec = call.Agent!;
        readonly string key = call.Step.IdempotencyKey;
        readonly AgentPaths paths = AgentPaths.Default;
        readonly List<string> events = [];
        readonly CancellationToken ct = context.CancellationToken;
        RepoRef repo = null!;
        string workBranch = "";
        string hiddenRef = "";
        string startRevision = "";
        string baseline = "";
        string? hiddenRevision;
        string lastPushed = "";
        SandboxHandle sandbox = null!;
        bool hasSandbox;
        bool reattached;
        DateTimeOffset deadline = DateTimeOffset.MaxValue;
        WorkingCopyRef copy = null!;
        AgentTask task = null!;
        AgentLimits limits = null!;

        bool Writes => spec.WriteAccess != "none" && spec.Push;

        public async Task<StepOutcome> ExecuteAsync()
        {
            if (spec.Branches.Count == 0)
                throw new StepCannotAnswerException("the agent step names no repository");
            repo = new RepoRef(spec.Branches.Keys.First());
            workBranch = spec.Branches[repo.FullName];
            hiddenRef = HiddenReference(call.Step);
            await RecordAsync(new RecordBatch(AgentSteps:
            [
                new AgentStepRecord(key, call.Step.RunId, spec.WorkflowId, call.Step.StepId, spec.Level, call.Contract, spec.CorrectionTurns, spec.QuestionsMax,
                    (long)spec.QuestionsTimeout.TotalSeconds, DateTimeOffset.UtcNow),
            ]));
            var token = owner.tokens.Issue(new StepClaims(key, call.Step.RunId, spec.WorkflowId, call.Step.StepId, spec.WriteAccess, spec.Tools,
                DateTimeOffset.UtcNow.Add((call.Timeout ?? TimeSpan.FromHours(4)) + TimeSpan.FromHours(2)).ToUnixTimeSeconds()));
            limits = new AgentLimits(call.Timeout, spec.CorrectionTurns, spec.QuestionsTimeout);
            task = new AgentTask(call.Step, call.What, null, call.Inputs, call.Contract)
            {
                Tools = spec.Tools,
                WriteAccess = spec.WriteAccess,
                Engine = new EngineEndpoints(owner.options.AgentUrls, owner.options.ModelBaseUrl, token),
                Paths = paths,
                Questions = spec.QuestionsMax > 0,
            };

            try
            {
                var interrupted = await PrepareAsync();
                deadline = await DeadlineAsync();
                var report = await SuperviseAndCollectAsync(interrupted);
                var outcome = await FinishAsync(report);
                await RevokeAsync();
                return outcome;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && hasSandbox && context.CancelReason is ActivityCancelReason.CancelRequested or ActivityCancelReason.GoneFromServer)
            {
                // The step was cancelled (a decided join, an abort): stop the agent, keep its commits on the hidden
                // reference for a later attempt, and remove the sandbox. The workflow waits for this to finish.
                // A worker shutting down is not a cancellation of the step: the sandbox is left for the next worker.
                // A run that has gone (it ended without waiting) will not resume the step either.
                await StopEverythingAsync();
                await RevokeAsync();
                throw;
            }
            catch (Exception e) when (e is StepWorkException or StepCannotAnswerException)
            {
                // The attempt has ended: its commits stay on the hidden reference, its sandbox goes, its token is revoked.
                if (hasSandbox)
                    await KeepCommitsAndDisposeAsync();
                await RevokeAsync();
                throw;
            }
        }

        /// <summary>A step's token stops working when its attempt ends (the endpoint refuses a revoked step).</summary>
        Task RevokeAsync() => RecordAsync(new RecordBatch(AgentSteps:
        [
            new AgentStepRecord(key, call.Step.RunId, spec.WorkflowId, call.Step.StepId, spec.Level, call.Contract, spec.CorrectionTurns, spec.QuestionsMax,
                (long)spec.QuestionsTimeout.TotalSeconds, DateTimeOffset.UtcNow, Revoked: true),
        ]));

        /// <summary>
        /// The step's deadline, <c>limits.timeout</c> from its first attempt (default 4 hours), kept in the record store so
        /// a retry after the activity's own timeout does not start the clock again.
        /// </summary>
        async Task<DateTimeOffset> DeadlineAsync()
        {
            if ((await EventsAsync()).FirstOrDefault(e => e.Kind == "deadline") is { } recorded)
                return DateTimeOffset.Parse(recorded.Detail, System.Globalization.CultureInfo.InvariantCulture);
            var at = DateTimeOffset.UtcNow + (call.Timeout ?? TimeSpan.FromHours(4));
            await EventAsync("deadline", at.ToString("O"));
            return at;
        }

        /// <summary>docs/04 section 1: an agent that ran out of time is a work result. Its commits are kept on the hidden reference.</summary>
        async Task OutOfTimeAsync(AgentSession? session)
        {
            if (session is not null)
                await runtime.StopAsync(session, CancellationToken.None);
            throw new StepWorkException($"the step ran out of time: its limit of {(call.Timeout ?? TimeSpan.FromHours(4)).TotalMinutes:F0} minutes passed at {deadline:O}");
        }

        static string HiddenReference(StepIdentity step) => $"refs/feature-flow/wip/{Safe(step.RunId)}/{Safe(step.StepId)}";

        /// <summary>Where a hidden reference's commits are kept when a later attempt does not build on them.</summary>
        public static string SupersededReference(StepIdentity step, string revision) =>
            $"refs/feature-flow/superseded/{Safe(step.RunId)}/{Safe(step.StepId)}/{revision[..Math.Min(12, revision.Length)]}";

        static string Safe(string text) => string.Concat(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

        /// <summary>Creates or re-attaches to the sandbox. Returns whether an earlier attempt's commits were checked out.</summary>
        async Task<bool> PrepareAsync()
        {
            var parent = spec.ParentBranches.GetValueOrDefault(repo.FullName, "main");
            baseline = Must(await vcs.ResolveAsync(repo, $"refs/heads/{parent}", ct), "read the parent branch")
                       ?? throw new StepCannotAnswerException($"{repo.FullName} has no branch {parent}");
            var branchRevision = Must(await vcs.ResolveAsync(repo, $"refs/heads/{workBranch}", ct), "read the work branch");
            if (branchRevision is null)
            {
                // The engine creates the run's work branch the first time a step touches the repository (docs/04 section 2).
                Must(await vcs.PushAsync(repo, $"refs/heads/{workBranch}", baseline, null, null, ct), "create the work branch");
                events.Add($"created branch {workBranch} at {Short(baseline)}");
                branchRevision = baseline;
            }

            startRevision = branchRevision;
            hiddenRevision = Must(await vcs.ResolveAsync(repo, hiddenRef, ct), "read the hidden reference");
            // An earlier attempt's commits are used only when they build on the branch as it is now. A hidden
            // reference behind the branch (the branch moved on, say by a catch-up) would rewind it (LOG 96).
            var useHidden = hiddenRevision is not null && hiddenRevision != startRevision
                            && Must(await vcs.DescendsFromAsync(repo, hiddenRevision, startRevision, ct), "compare the hidden reference with the work branch");
            if (hiddenRevision is not null && hiddenRevision != startRevision && !useHidden)
            {
                // The earlier attempt's commits are kept for a person (docs/04 section 3.7) under a reference of their own
                // before this attempt's pushes replace the hidden reference (LOG 96).
                var kept = SupersededReference(call.Step, hiddenRevision);
                if (Must(await vcs.ResolveAsync(repo, kept, ct), "read the superseded reference") is null)
                    Must(await vcs.PushAsync(repo, kept, hiddenRevision, null, null, ct), "keep the superseded commits");
                events.Add($"hidden reference at {Short(hiddenRevision)} does not build on {workBranch} at {Short(startRevision)}: not used; kept as {kept}");
                await EventAsync("superseded", $"{hiddenRevision} as {kept}");
            }
            lastPushed = useHidden ? hiddenRevision! : startRevision;
            copy = new WorkingCopyRef(SandboxHandle.None, paths.Checkout);

            var attached = await sandboxes.AttachAsync(key, ct);
            if (attached.Ok && !(await EventsAsync()).Any(e => e.Kind == "sandbox" && e.Detail.StartsWith("created ", StringComparison.Ordinal)))
            {
                // A sandbox whose preparation did not finish is removed and made again.
                await sandboxes.DisposeAsync(attached.Value!, CancellationToken.None);
                attached = ProviderResult<SandboxHandle>.Failure("not_found", "the sandbox was not fully prepared");
            }

            if (attached.Ok)
            {
                sandbox = attached.Value!;
                hasSandbox = true;
                reattached = true;
                copy = copy with { Sandbox = sandbox };
                events.Add("re-attached to the sandbox");
                await EventAsync("sandbox", "re-attached " + sandbox.Id);
                return false;
            }

            if (attached.Error!.Reason != "not_found")
                Must(attached, "attach to the sandbox");
            sandbox = Must(await sandboxes.CreateAsync(new SandboxSpec(key, paths.Home, new Dictionary<string, string>(),
                [paths.Checkout, paths.Output, "/ff/inputs", "/ff/state", "/ff/tmp"]), ct), "create the sandbox");
            hasSandbox = true;
            copy = copy with { Sandbox = sandbox };
            // Root-owned and read-only. It runs as the agent's user, so its marker and log in /ff/state are hints the
            // agent could write: the engine decides "engine unreachable" from its own records (LOG 98).
            Must(await sandboxes.CopyInAsync(sandbox, await owner.ClientAsync(ct), paths.Client, readOnly: true, ct), "copy in the sandbox client");
            await AgentShellAsync("git config --global user.name 'Feature Flow agent' && git config --global user.email agent@feature-flow.invalid"
                                  + " && git config --global safe.directory '*' && git config --global init.defaultBranch main", "configure git");

            var source = useHidden ? hiddenRevision! : startRevision;
            Must(await vcs.CheckOutAsync(repo, useHidden ? hiddenRef : $"refs/heads/{workBranch}", source, workBranch, copy, ct), "check out the work branch");
            await StageInputsAsync();
            if (spec.WriteAccess == "none")
                Must(await sandboxes.MakeReadOnlyAsync(sandbox, paths.Checkout, ct), "make the checkout read-only");
            events.Add($"sandbox created; checkout of {workBranch} at {Short(source)}" + (useHidden ? " from the hidden reference" : ""));
            await EventAsync("sandbox", "created " + sandbox.Id);
            return useHidden;
        }

        /// <summary>The inputs manifest (docs/04 section 3.1): inputs, consumed artifacts and skills staged read-only, the baseline, branches and output folder.</summary>
        async Task StageInputsAsync()
        {
            var staged = new List<object>();
            foreach (var (name, reference) in spec.Consumed)
            {
                var path = $"/ff/inputs/artifacts/{name}";
                Must(await sandboxes.CopyInAsync(sandbox, new ArtifactRef(reference), path, readOnly: true, ct), $"stage artifact {name}");
                staged.Add(new { name, path });
            }

            var skills = new List<object>();
            foreach (var (skill, files) in await SkillsAsync())
                foreach (var (file, text) in files)
                {
                    var put = Must(await owner.Store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), ct));
                    var path = $"/ff/inputs/skills/{skill}/{file}";
                    Must(await sandboxes.CopyInAsync(sandbox, put, path, readOnly: true, ct), $"stage skill {skill}");
                    if (file == "SKILL.md")
                        skills.Add(new { name = skill, path });
                }

            var manifest = new
            {
                step = call.Step.StepId,
                repository = repo.FullName,
                checkout = paths.Checkout,
                work_branch = workBranch,
                parent_branch = spec.ParentBranches.GetValueOrDefault(repo.FullName, "main"),
                baseline_commit = baseline,
                output_folder = paths.Output,
                inputs = call.Inputs.ToDictionary(kv => kv.Key, kv => JsonDocument.Parse(JsonOf(kv.Value)).RootElement),
                artifacts = staged,
                skills,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
            Must(await sandboxes.CopyInAsync(sandbox, Must(await owner.Store.PutAsync(new MemoryStream(bytes), ct)), paths.InputsManifest, readOnly: true, ct),
                "stage the inputs manifest");
        }

        /// <summary>An input value as JSON. Inputs reach the activity as YAML plain text, which is JSON for scalars, lists and maps.</summary>
        static string JsonOf(string plain)
        {
            try
            {
                using var _ = JsonDocument.Parse(plain);
                return plain;
            }
            catch (JsonException)
            {
                return JsonSerializer.Serialize(plain);
            }
        }

        /// <summary>The step's skills, each a folder of files from the run's library lock.</summary>
        async Task<List<(string Name, List<(string File, string Text)> Files)>> SkillsAsync()
        {
            var list = new List<(string, List<(string, string)>)>();
            if (spec.Skills.Count == 0)
                return list;
            var lockDocument = JsonSerializer.Deserialize<LibraryLock>(await TextAsync(spec.LockRef), Json)!;
            foreach (var reference in spec.Skills.Select(LibraryReference.TryParse).OfType<LibraryReference>())
            {
                var library = lockDocument.Libraries.FirstOrDefault(l => l.Name == reference.Library);
                if (library is null)
                    continue;
                var prefix = $"skills/{reference.Name}/";
                var files = new List<(string, string)>();
                foreach (var (path, hash) in library.Files.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal)))
                    files.Add((path[prefix.Length..], await TextAsync(hash)));
                list.Add((reference.Name, files));
            }

            return list;
        }

        async Task<string> TextAsync(string reference)
        {
            await using var stream = Must(await owner.Store.GetAsync(new ArtifactRef(reference), ct));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }

        /// <summary>The first prompt: the library's reporting preamble, where the step's files are, and the rendered task.</summary>
        async Task<string> PromptAsync(bool interrupted)
        {
            var preamble = await PreambleAsync();
            var text = new StringBuilder();
            if (preamble is not null)
                text.Append(preamble.TrimEnd()).Append("\n\n");
            text.Append("## This step\n\n")
                .Append($"- Your working directory is the checkout of `{repo.FullName}` on branch `{workBranch}`: `{paths.Checkout}`.")
                .Append(spec.WriteAccess == "none" ? " It is read-only.\n" : "\n")
                .Append($"- Your output folder is `{paths.Output}`.\n")
                .Append($"- The inputs manifest is `{paths.InputsManifest}`.\n");
            if (interrupted)
                text.Append("- An earlier attempt of this step was interrupted. Its commits are already in the checkout: continue from them.\n");
            text.Append('\n').Append(call.Agent!.TaskText ?? $"Do the work of the {call.What} role.");
            return text.ToString();
        }

        async Task<string?> PreambleAsync()
        {
            if (spec.TaskLibrary is null)
                return null;
            var lockDocument = JsonSerializer.Deserialize<LibraryLock>(await TextAsync(spec.LockRef), Json)!;
            var library = lockDocument.Libraries.FirstOrDefault(l => l.Name == spec.TaskLibrary);
            return library?.Files.TryGetValue("prompts/reporting_preamble.md", out var hash) == true ? await TextAsync(hash) : null;
        }

        async Task<IReadOnlyList<AgentEventRecord>> EventsAsync() =>
            Must(await owner.Records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = key }, ct));

        async Task<IReadOnlyList<ReportRecord>> ReportsAsync() =>
            Must(await owner.Records.QueryAsync<ReportRecord>(Queries.Reports, new Dictionary<string, string> { ["step_key"] = key }, ct));

        async Task RecordAsync(RecordBatch batch) => Must(await owner.Records.AppendAsync(batch, CancellationToken.None));

        Task EventAsync(string kind, string detail) => RecordAsync(new RecordBatch(AgentEvents: [new AgentEventRecord(key, kind, detail, DateTimeOffset.UtcNow)]));

        /// <summary>
        /// Starts or re-attaches to the session, supervises it, and collects a report, continuing the session with
        /// the errors of a rejected report while correction turns remain. The record store's session events say
        /// which session is current, so a new worker picks up where the last left off.
        /// </summary>
        async Task<StepReport> SuperviseAndCollectAsync(bool interrupted)
        {
            var (session, number, message) = await LatestSessionAsync();
            AgentSession? running = null;
            if (DateTimeOffset.UtcNow >= deadline)
                await OutOfTimeAsync(null);
            if (session is not null && message is null && reattached)
            {
                var attached = await runtime.AttachAsync(sandbox, task with { Session = number }, ct);
                if (attached.Ok)
                    running = attached.Value;
                else
                    message = Interrupted;
            }
            else if (session is not null && !reattached)
                // A new sandbox holds no session to resume: a fresh session, told the attempt was interrupted (docs/04 section 3.2).
                message = Interrupted;

            if (message == Interrupted)
                await CheckResumesAsync();
            var continuations = 0;
            var fresh = !reattached;
            while (true)
            {
                if (running is null)
                {
                    if (number == 0 || fresh)
                    {
                        number++;
                        // docs/03 section 6.3 (P7): the rendered task is stored as an artifact and its hash recorded with the attempt.
                        var prompt = await PromptAsync(interrupted || number > 1);
                        var stored = Must(await owner.Store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(prompt)), ct), "store the prompt");
                        events.Add($"prompt of session {number}: {stored.Id}");
                        await EventAsync("prompt", stored.Id);
                        running = Must(await runtime.StartAsync(sandbox, task with { Session = number, TaskText = prompt }, limits, ct), "start the agent");
                        fresh = false;
                    }
                    else
                    {
                        number++;
                        running = Must(await runtime.ContinueAsync(sandbox, task with { Session = number }, limits, message!, ct), "continue the session");
                    }

                    await EventAsync("session", $"{number} started");
                    events.Add($"session {number} started");
                }

                await SuperviseAsync(running);
                var result = Must(await runtime.GetResultAsync(running, ct), "read the session's result");
                if (result.End == SessionEnd.EngineUnreachable && await LastContactAsync() is { } contact
                    && DateTimeOffset.UtcNow - contact < (result.QuietWindow ?? TimeSpan.FromSeconds(60)))
                    // The marker is the agent's to write: the engine believes an outage only when its own records show
                    // no call from the step for as long as the client holds before it gives up (LOG 98).
                    result = result with
                    {
                        End = SessionEnd.Failed,
                        Detail = $"the session claimed the engine was unreachable, but the engine took a call from it at {contact:O}",
                    };
                await EventAsync("session", $"{number} ended {result.End}");
                if (result.Transcript is { } transcript)
                    events.Add($"transcript of session {number}: {transcript}");
                await PushCommitsAsync();
                if (result.End is SessionEnd.ModelUnreachable or SessionEnd.EngineUnreachable or SessionEnd.Lost)
                    throw new ApplicationFailureException($"the session stopped resumably: {result.Detail}", errorType: "infrastructure");
                if (result.End == SessionEnd.ModelRefused)
                    throw new StepCannotAnswerException($"the model refused the step's requests, a configuration fault: {result.Detail}");

                var reports = await ReportsAsync();
                if (reports.LastOrDefault(r => r.Accepted)?.Report is { } accepted)
                    return accepted;
                var rejected = reports.Count(r => !r.Accepted);
                if (result.ReportFile is { } file && rejected <= spec.CorrectionTurns)
                {
                    var (checkedReport, errors) = await CheckFileReportAsync(file);
                    await RecordAsync(new RecordBatch(Reports: [new ReportRecord(key, reports.Count + 1, checkedReport is not null, checkedReport, errors, DateTimeOffset.UtcNow)]));
                    if (checkedReport is not null)
                        return checkedReport;
                    rejected++;
                    if (rejected <= spec.CorrectionTurns)
                    {
                        message = $"The engine rejected report.json:\n{string.Join("\n", errors.Select(e => "- " + e))}\n" +
                                  $"Fix what it names and write report.json again (correction turn {rejected} of {spec.CorrectionTurns}).";
                        running = null;
                        continue;
                    }
                }
                else if (result.Report is { } own)
                    return own;

                if (rejected > spec.CorrectionTurns)
                    throw new StepWorkException($"the report was rejected {rejected} times; the attempt has failed: " +
                                                string.Join("; ", reports.LastOrDefault(r => !r.Accepted)?.Errors ?? []));
                if (rejected > 0 && reports.Last() is { Accepted: false } last && continuations++ < spec.CorrectionTurns)
                {
                    // The agent stopped after a rejection without correcting: give it the errors in the same session.
                    message = $"The engine rejected your report:\n{string.Join("\n", last.Errors.Select(e => "- " + e))}\nFix what it names and call report again.";
                    running = null;
                    continue;
                }

                throw new StepWorkException("the agent ended without reporting" + (result.Detail is { Length: > 0 } d ? $" ({result.End}: {Trim(d)})" : ""));
            }
        }

        /// <summary>The last time the agent worker took a call for this step: a hook, a model request or a report.</summary>
        async Task<DateTimeOffset?> LastContactAsync()
        {
            var calls = (await EventsAsync()).Where(e => e.Kind.StartsWith("hook_", StringComparison.Ordinal) || e.Kind == "model").Select(e => e.At)
                .Concat((await ReportsAsync()).Select(r => r.At)).ToList();
            return calls.Count == 0 ? null : calls.Max();
        }

        const string Interrupted ="Your previous session was interrupted. Carry on with the task from where you were.";

        /// <summary>At most this many resumes in a row without progress, and this many in all (LOG 98).</summary>
        public const int ResumesWithoutProgress = 5;

        public const int Resumes = 20;

        /// <summary>
        /// A resume is free only when the stopped session made progress: a tool call or a pushed commit (docs/04 section
        /// 3.2). Past the caps the step stops for a person, rather than resuming for ever.
        /// </summary>
        async Task CheckResumesAsync()
        {
            var all = await EventsAsync();
            var stops = new List<bool>();
            for (var i = 0; i < all.Count; i++)
            {
                if (all[i].Kind != "session" || !all[i].Detail.EndsWith(SessionEnd.ModelUnreachable, StringComparison.Ordinal)
                    && !all[i].Detail.EndsWith(SessionEnd.EngineUnreachable, StringComparison.Ordinal) && !all[i].Detail.EndsWith(SessionEnd.Lost, StringComparison.Ordinal))
                    continue;
                var number = all[i].Detail.Split(' ')[0];
                var started = all.Take(i).LastOrDefault(e => e is { Kind: "session" } && e.Detail == $"{number} started");
                var startIndex = started is null ? 0 : all.ToList().IndexOf(started);
                stops.Add(all.Skip(startIndex).Take(i - startIndex).Any(e => e.Kind is "hook_pre_tool_use" or "pushed"));
            }

            var trailing = stops.AsEnumerable().Reverse().TakeWhile(p => !p).Count();
            if (trailing >= ResumesWithoutProgress)
                throw new StepCannotAnswerException($"the agent's session stopped {trailing} times in a row without progress; a person must look");
            if (stops.Count >= Resumes)
                throw new StepCannotAnswerException($"the agent's session stopped {stops.Count} times; a person must look");
        }

        static string Trim(string text) => text.Length > 300 ? text[..300] + "..." : text;

        /// <summary>The current session from the record store: its number, and the message to continue it with when it ended resumably.</summary>
        async Task<(string? Session, int Number, string? Message)> LatestSessionAsync()
        {
            var sessions = (await EventsAsync()).Where(e => e.Kind == "session").Select(e => e.Detail.Split(' ')).ToList();
            if (sessions.Count == 0)
                return (null, 0, null);
            var last = sessions.Last();
            var number = int.Parse(last[0], System.Globalization.CultureInfo.InvariantCulture);
            if (last[1] == "started")
                return ($"{number}", number, null);
            // A session that stopped resumably is continued in a new one; one that ended otherwise is collected again.
            return last.ElementAtOrDefault(2) is SessionEnd.ModelUnreachable or SessionEnd.EngineUnreachable or SessionEnd.Lost
                ? ($"{number}", number, Interrupted)
                : ($"{number}", number, null);
        }

        /// <summary>Heartbeats and pushes new commits to the hidden reference until the session ends (docs/02 section 4.3).</summary>
        async Task SuperviseAsync(AgentSession session)
        {
            var nextPush = DateTimeOffset.UtcNow + owner.options.CommitInterval;
            while (true)
            {
                context.Heartbeat();
                var progress = Must(await runtime.GetProgressAsync(session, ct), "read the session's progress");
                if (progress.Finished)
                    return;
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    await PushCommitsAsync();
                    await OutOfTimeAsync(session);
                }
                if (DateTimeOffset.UtcNow >= nextPush)
                {
                    await PushCommitsAsync();
                    nextPush = DateTimeOffset.UtcNow + owner.options.CommitInterval;
                }

                await Task.Delay(owner.options.PollInterval ?? TimeSpan.FromSeconds(1), ct);
            }
        }

        /// <summary>Pushes commits the working copy has beyond the last push to the hidden reference, as a bundle, with the expected old revision.</summary>
        async Task PushCommitsAsync()
        {
            if (!Writes)
                return;
            var head = await vcs.GetRevisionAsync(copy, ct);
            if (!head.Ok || head.Value == lastPushed)
                return;
            var bundle = Must(await vcs.FetchCommitsAsync(copy, lastPushed, ct), "take the commits out of the sandbox");
            Must(await vcs.PushAsync(repo, hiddenRef, bundle.Head, bundle, hiddenRevision, ct), "push to the hidden reference");
            hiddenRevision = lastPushed = bundle.Head;
            events.Add($"pushed {Short(bundle.Head)} to {hiddenRef}");
            await EventAsync("pushed", $"{bundle.Head} to {hiddenRef}");
        }

        async Task<(StepReport? Report, IReadOnlyList<string> Errors)> CheckFileReportAsync(string text)
        {
            JsonElement report;
            try
            {
                report = JsonDocument.Parse(text).RootElement;
            }
            catch (JsonException e)
            {
                return (null, [$"report.json is not JSON: {e.Message}"]);
            }

            var missing = new List<string>();
            foreach (var path in ReportCheck.Paths(report))
            {
                var exists = await sandboxes.RunAsync(sandbox, new SandboxCommand(["test", "-s", $"{paths.Output}/{path}"]), $"{key}/exists/{Guid.NewGuid():N}", ct);
                if (!exists.Ok || exists.Value.Exit != 0)
                    missing.Add(path);
            }

            return ReportCheck.Check(report, call.Contract, missing);
        }

        /// <summary>
        /// Commits leftovers (unless <c>on_uncommitted: fail</c>), checks a read-only role committed nothing, pushes,
        /// moves the work branch once, copies the artifacts out, publishes the engine's outputs, and removes the sandbox.
        /// </summary>
        async Task<StepOutcome> FinishAsync(StepReport report)
        {
            if (spec.WriteAccess != "none")
            {
                if (Must(await vcs.IsDirtyAsync(copy, ct), "read the checkout's state"))
                {
                    if (spec.OnUncommitted == "fail")
                        throw new StepWorkException("the agent left uncommitted changes and the step has on_uncommitted: fail");
                    var committed = Must(await vcs.CommitAllAsync(copy, $"Uncommitted changes left by {call.Step.StepId}", ct), "commit leftovers");
                    events.Add($"committed leftovers as {Short(committed ?? "")}");
                }
            }

            var head = Must(await vcs.GetRevisionAsync(copy, ct), "read the checkout's revision");
            if (spec.WriteAccess == "none" && head != startRevision)
                throw new StepWorkException("a role with no write access committed: nothing it changed is kept");
            await PushCommitsAsync();

            var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, path) in report.Artifacts)
            {
                artifacts[name] = Must(await sandboxes.CopyOutAsync(sandbox, $"{paths.Output}/{path}", ct), $"copy out artifact {name}").Id;
                events.Add($"artifact {name}: {artifacts[name]}");
            }

            var outputs = report.Outputs.ToDictionary(kv => kv.Key, kv => kv.Value);
            var commits = head == startRevision ? 0 : Must(await vcs.CountCommitsAsync(copy, startRevision, head, ct), "count the step's commits");
            var changed = Must(await vcs.ChangedFilesAsync(copy, baseline, head, ct), "list the changed files");
            var stepEvents = await EventsAsync();
            var opened = stepEvents.Where(e => e.Kind == "hook_post_tool_use" && e.Detail.Contains(' ', StringComparison.Ordinal))
                .Select(e => Relative(e.Detail[(e.Detail.IndexOf(' ', StringComparison.Ordinal) + 1)..])).ToHashSet(StringComparer.Ordinal);
            var hooked = stepEvents.Any(e => e.Kind == "hook_pre_tool_use");
            if (call.Contract.OutputTypes.ContainsKey("commits"))
                outputs["commits"] = commits.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (call.Contract.OutputTypes.ContainsKey("changed_files_unread"))
                outputs["changed_files_unread"] = changed.Count(f => !opened.Contains(f)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (call.Contract.OutputTypes.ContainsKey("coverage_known"))
                outputs["coverage_known"] = hooked ? "true" : "false";
            events.Add($"engine outputs: commits={commits} changed_files_unread={changed.Count(f => !opened.Contains(f))} coverage_known={(hooked ? "true" : "false")}");

            if (Writes && head != startRevision)
            {
                // The work branch moves once, to the final commit (docs/04 section 3.1, step 7), and only forward: the
                // final commit must build on the branch as the step found it (LOG 96).
                if (!Must(await vcs.DescendsFromAsync(repo, head, startRevision, ct), "compare the final commit with the work branch"))
                    throw new StepWorkException($"the final commit {Short(head)} does not build on {workBranch} at {Short(startRevision)}; the branch is not moved");
                Must(await vcs.PushAsync(repo, $"refs/heads/{workBranch}", head, null, startRevision, ct), "move the work branch");
                events.Add($"moved {workBranch} to {Short(head)}");
                await EventAsync("moved", $"{workBranch} to {head}");
            }

            // The sandbox client's log of its calls to the engine, kept for diagnosis (reconnections, held hooks).
            if (await sandboxes.CopyOutAsync(sandbox, "/ff/state/client.jsonl", ct) is { Ok: true } log)
                events.Add($"sandbox client log: {log.Value!.Id}");
            await sandboxes.DisposeAsync(sandbox, CancellationToken.None);
            return new StepOutcome(StepOutcome.Reported, report with { Outputs = outputs, Artifacts = artifacts }, null, events);
        }

        string Relative(string path) => path.StartsWith(paths.Checkout + "/", StringComparison.Ordinal) ? path[(paths.Checkout.Length + 1)..] : path;

        async Task KeepCommitsAndDisposeAsync()
        {
            try
            {
                await PushCommitsAsync();
            }
            catch (Exception e) when (e is ApplicationFailureException or StepWorkException)
            {
            }

            await sandboxes.DisposeAsync(sandbox, CancellationToken.None);
        }

        async Task StopEverythingAsync()
        {
            var (session, number, _) = await LatestSessionAsync();
            if (session is not null && (await runtime.AttachAsync(sandbox, task with { Session = Math.Max(number, 1) }, CancellationToken.None)) is { Ok: true } running)
                await runtime.StopAsync(running.Value!, CancellationToken.None);
            try
            {
                var head = await vcs.GetRevisionAsync(copy, CancellationToken.None);
                if (Writes && head.Ok && head.Value != lastPushed)
                {
                    var bundle = await vcs.FetchCommitsAsync(copy, lastPushed, CancellationToken.None);
                    if (bundle.Ok)
                        await vcs.PushAsync(repo, hiddenRef, bundle.Value!.Head, bundle.Value, hiddenRevision, CancellationToken.None);
                }
            }
            finally
            {
                await sandboxes.DisposeAsync(sandbox, CancellationToken.None);
                await EventAsync("cancelled", "the step was cancelled; its sandbox was removed");
            }
        }

        async Task AgentShellAsync(string script, string what)
        {
            var run = Must(await sandboxes.RunAsync(sandbox, new SandboxCommand(["sh", "-c", script]), $"{key}/{Guid.NewGuid():N}", ct), what);
            if (run.Exit != 0)
                throw new ApplicationFailureException($"could not {what} in the sandbox: {run.Output.Trim()}", errorType: "infrastructure");
        }

        static string Short(string revision) => revision.Length > 10 ? revision[..10] : revision;
    }
}
