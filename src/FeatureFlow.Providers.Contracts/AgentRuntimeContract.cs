using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>A tool call in a scripted session, by abstract class (<see cref="ToolClass"/>), with the file or command it names.</summary>
public sealed record CliAction(string Class, string? Path = null, string? Command = null);

/// <summary>
/// What a scripted session does, independent of the CLI: its tool calls, its last text and usage, and how it ends
/// (an exit code, a model API error and its status, still running, or lost with its sandbox).
/// </summary>
public sealed record CliScript(IReadOnlyList<CliAction> Actions, string Text = "Done.", long InputTokens = 0, long OutputTokens = 0, int Exit = 0,
    string? ApiError = null, int? ApiStatus = null, bool Running = false, bool Lost = false);

/// <summary>How the runtime started its CLI: the arguments, the file on standard input, the environment and folder, and every file in the sandbox.</summary>
public sealed record CliInvocation(IReadOnlyList<string> Arguments, string? Stdin, IReadOnlyDictionary<string, string> Environment, string? Directory,
    IReadOnlyDictionary<string, string> Files);

/// <summary>What the CLI printed, and the files it wrote in the sandbox.</summary>
public sealed record CliRun(string Output, IReadOnlyDictionary<string, string>? Files = null);

/// <summary>A hook command the CLI runs, as its settings name it, and its timeout.</summary>
public sealed record HookSetting(string Command, int TimeoutSeconds);

/// <summary>
/// The agent runtime's contract (docs/06 section 3.2; F12, LOG 447), for a runtime whose CLI runs in a sandbox. It drives
/// the real runtime against a scripted sandbox and a stand-in for its CLI, with no model, credential or container: the
/// runtime writes its files and starts its command line as it would in a real sandbox, and the stand-in plays the CLI
/// from them, reading its settings as the CLI would, and printing the CLI's output and sending its hook input for a
/// <see cref="CliScript"/>. The suite checks what the engine relies on: settings only the engine controls, calling the
/// sandbox client for every hook; no credential and no task text in the command; only the role's tool classes offered;
/// every tool name the CLI's hooks can report read as the class of the CLI's tool, by the stand-in's own table, so any
/// tool mapped to the wrong class fails; a denial the CLI blocks with the reason shown; the runtime's reply for an
/// unreachable engine, which the CLI blocks; and the session lifecycle: start, turns, the result, a continuation of the same session, re-attach, stop, and
/// how an API error, the engine unreachable, a lost process and a failed exit end the session.
/// <para>
/// A runtime's test project subclasses it with its stand-in. The stand-in's output and hook shapes are those its CLI was
/// seen to produce (Claude Code 2.1, Copilot CLI 1.0.105), so a runtime whose parser or hook mapping drifts from them fails.
/// </para>
/// </summary>
public abstract class AgentRuntimeContract : ContractBase
{
    /// <summary>The runtime under test, created on the given providers: it must also read its hooks (<see cref="IAgentHooks"/>).</summary>
    protected abstract IAgentRuntimeProvider CreateRuntime(ISandboxProvider sandboxes, IArtifactStoreProvider store);

    /// <summary>The hook commands the CLI runs, read from its settings files as the CLI reads them.</summary>
    protected abstract IReadOnlyList<HookSetting> Hooks(CliInvocation invocation);

    /// <summary>The engine MCP server's tool timeout, as the CLI reads it.</summary>
    protected abstract TimeSpan McpToolTimeout(CliInvocation invocation);

    /// <summary>The session the command line names, and whether it resumes it.</summary>
    protected abstract (string? Id, bool Resumes) Session(CliInvocation invocation);

    /// <summary>The CLI's tools the command line offers, other than the engine's.</summary>
    protected abstract IReadOnlyList<string> OfferedTools(CliInvocation invocation);

    /// <summary>The CLI's output for a script; each tool call, with its result, and the last text, as the CLI prints them.</summary>
    protected abstract CliRun Play(CliInvocation invocation, CliScript script);

    /// <summary>The hook input the CLI sends for a tool call (<c>tool_name</c>, <c>tool_input</c>).</summary>
    protected abstract JsonObject HookInput(CliAction action);

    /// <summary>The hook input the CLI sends when the agent calls one of the tools its command line offered.</summary>
    protected abstract JsonObject HookInputForTool(string tool);

    /// <summary>
    /// Every tool name the CLI's hooks can report, offered or brought with an offered tool, with the class of the CLI's
    /// tool: the stand-in's own table, written from the CLI, never taken from the runtime's.
    /// </summary>
    protected abstract IReadOnlyDictionary<string, string> CliTools { get; }

    /// <summary>
    /// How the CLI reads a hook's reply to a tool call: whether it blocks the call (or stops the session before it runs),
    /// and the text shown.
    /// </summary>
    protected abstract (bool Blocked, string? Shown) ReadReply(HookReply reply);

    readonly MemoryArtifacts store = new();
    readonly ScriptedCliSandbox sandboxes;
    readonly string taskText = "Contract task " + Guid.NewGuid().ToString("N");
    SandboxHandle sandbox = null!;
    IAgentRuntimeProvider runtime = null!;

    protected AgentRuntimeContract() => sandboxes = new ScriptedCliSandbox(store);

    const string StepToken = "ffs1.contract-step-token";
    const string ModelBaseUrl = "http://agent-worker.test:8490/model";
    static readonly AgentLimits Limits = new(null, 2, TimeSpan.FromHours(4));

    IAgentRuntimeProvider Runtime => runtime ??= CreateRuntime(sandboxes, store);

    IAgentHooks HookReader => Runtime as IAgentHooks ?? throw new InvalidOperationException("the runtime does not read its hooks (IAgentHooks)");

    protected AgentTask StepTask(string writeAccess = "branch", IReadOnlyList<string>? tools = null, int session = 1, string key = "run-1/implement/1") =>
        new(new StepIdentity("run-1", "t", "implement", 1, key), "implementer", taskText, new Dictionary<string, string>(),
            new StepContract([], new Dictionary<string, string>(), [], new Dictionary<string, string>()))
        {
            Tools = tools ?? [ToolClass.Read, ToolClass.Search, ToolClass.Edit, ToolClass.Shell],
            WriteAccess = writeAccess,
            Engine = new EngineEndpoints(["http://agent-worker.test:8490"], ModelBaseUrl, StepToken),
            Paths = AgentPaths.Default,
            Questions = true,
            Session = session,
        };

    async Task<(AgentSession Session, CliInvocation Invocation)> StartAsync(CliScript script, AgentTask? task = null, string? continuation = null)
    {
        sandbox ??= Ok(await sandboxes.CreateAsync(new SandboxSpec("contract", AgentPaths.Default.Home, new Dictionary<string, string>(), []), Ct));
        CliInvocation? seen = null;
        sandboxes.Cli = invocation =>
        {
            seen = invocation;
            return (Play(invocation, script), script);
        };
        var started = Ok(continuation is null
            ? await Runtime.StartAsync(sandbox, task ?? StepTask(), Limits, Ct)
            : await Runtime.ContinueAsync(sandbox, task ?? StepTask(), Limits, continuation, Ct));
        return (started, seen ?? throw new InvalidOperationException("the runtime started no command"));
    }

    static readonly CliScript Simple = new([new CliAction(ToolClass.Read, "/ff/work/a.txt")], "All done.", 5, 7);

    [Fact]
    public async Task The_settings_are_written_read_only_and_call_the_sandbox_client_for_every_hook()
    {
        var (_, cli) = await StartAsync(Simple);

        Assert.All(sandboxes.Written, f => Assert.True(f.ReadOnly, $"{f.Path} is not read-only"));
        var hooks = Hooks(cli);
        Assert.Equal(["post_tool_use", "pre_tool_use", "session_start", "stop"],
            hooks.Select(h => h.Command).Order(StringComparer.Ordinal).Select(c => c.Replace(SandboxedAgentRuntime.HookCommand(""), "", StringComparison.Ordinal)));
        Assert.All(hooks, h => Assert.StartsWith(SandboxedAgentRuntime.HookCommand(""), h.Command, StringComparison.Ordinal));
        // DR16: the hook's timeout exceeds the client's hold; the MCP tool timeout exceeds the longest a question waits.
        var hold = int.Parse(cli.Environment["FF_HOOK_HOLD_SECONDS"], System.Globalization.CultureInfo.InvariantCulture);
        Assert.All(hooks, h => Assert.True(h.TimeoutSeconds > hold, $"{h.Command}: timeout {h.TimeoutSeconds} s is not over the hold {hold} s"));
        Assert.True(McpToolTimeout(cli) > Limits.QuestionsTimeout);
    }

    /// <summary>F6, DR19: the step's token and the proxy's URL, no model or code host credential, the engine's home; the task only in the prompt file.</summary>
    [Fact]
    public async Task The_cli_runs_in_the_checkout_with_the_step_token_and_no_credential_and_reads_the_task_from_a_file()
    {
        var (_, cli) = await StartAsync(Simple);

        Assert.Equal(AgentPaths.Default.Checkout, cli.Directory);
        Assert.Contains(StepToken, cli.Environment.Values);
        Assert.Contains(ModelBaseUrl, cli.Environment.Values);
        Assert.Equal(AgentPaths.Default.Home, cli.Environment["HOME"]);
        foreach (var name in new[] { "ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "OPENAI_API_KEY", "GH_TOKEN", "GITHUB_TOKEN", "COPILOT_GITHUB_TOKEN" })
            Assert.DoesNotContain(name, cli.Environment.Keys);
        Assert.DoesNotContain(cli.Arguments, a => a.Contains(taskText, StringComparison.Ordinal));
        Assert.Equal(taskText, cli.Files[cli.Stdin ?? throw new InvalidOperationException("the prompt is not on standard input")]);
    }

    /// <summary>
    /// LOG 65: the command line offers the role's tool classes and no others; a role with no write access, no editing tool.
    /// Each offered tool is one the CLI has, and its class is the CLI's, by the stand-in's own table.
    /// </summary>
    [Theory]
    [InlineData("branch", new[] { "read", "search" }, new[] { "read", "search" })]
    [InlineData("branch", new[] { "read", "search", "edit", "shell" }, new[] { "edit", "read", "search", "shell" })]
    [InlineData("none", new[] { "read", "search", "edit", "shell" }, new[] { "read", "search", "shell" })]
    public async Task Only_the_role_s_tool_classes_are_offered(string writeAccess, string[] role, string[] offered)
    {
        var (_, cli) = await StartAsync(Simple, StepTask(writeAccess, role));

        var names = OfferedTools(cli).Select(t => HookInputForTool(t)["tool_name"]!.GetValue<string>()).ToList();
        Assert.All(names, name => Assert.True(CliTools.ContainsKey(name), $"the command line offers {name}, a tool the CLI does not have"));
        Assert.Equal(offered, names.Select(name => CliTools[name]).Distinct().Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// LOG 444, 445: the runtime reads every tool name its CLI's hooks can report, offered or not, as the class of the CLI's
    /// tool, so a tool mapped to the wrong class, or not mapped, fails here, one tool at a time.
    /// </summary>
    [Fact]
    public void Every_tool_the_cli_reports_is_read_as_its_class()
    {
        Assert.NotEmpty(CliTools);
        Assert.All(CliTools, tool =>
        {
            var read = HookReader.Classify(Element(new JsonObject { ["tool_name"] = tool.Key, ["tool_input"] = new JsonObject() })).Class;
            Assert.True(read == tool.Value, $"{tool.Key} is read as {read ?? "no class"}; the CLI's {tool.Key} is {tool.Value}");
        });
    }

    /// <summary>
    /// The runtime reads its CLI's hook input with the file or command, so the engine's policy names no runtime's tools.
    /// </summary>
    [Theory]
    [InlineData("read", "/ff/work/src/a.cs", null)]
    [InlineData("search", null, null)]
    [InlineData("edit", "/ff/work/src/b.cs", null)]
    [InlineData("shell", null, "git status")]
    [InlineData("engine", null, null)]
    public void Each_tool_call_s_hook_input_is_read_as_its_class(string toolClass, string? path, string? command)
    {
        var call = HookReader.Classify(Element(HookInput(new CliAction(toolClass, path, command))));

        Assert.Equal(toolClass, call.Class);
        Assert.Equal(path, call.Path);
        Assert.Equal(command, call.Command);
        Assert.False(string.IsNullOrEmpty(call.Tool));
    }

    [Fact]
    public void A_tool_the_runtime_does_not_map_has_no_class() =>
        Assert.Null(HookReader.Classify(Element(new JsonObject { ["tool_name"] = "WebFetch", ["tool_input"] = new JsonObject { ["url"] = "https://example.com" } })).Class);

    /// <summary>The CLI blocks a denied call and shows the agent the engine's reason (LOG 293: Copilot shows nothing for an exit 2).</summary>
    [Fact]
    public void A_denial_blocks_the_call_and_shows_the_agent_the_reason() =>
        Assert.Equal((true, "never push: commit your work and the engine pushes it"), ReadReply(HookReader.Deny("never push: commit your work and the engine pushes it")));

    /// <summary>
    /// LOG 444, R19: when no replica answers, the hook command applies the reply the runtime gave it in the CLI's
    /// environment, and the CLI blocks the call or stops the session: the hook fails closed.
    /// </summary>
    [Fact]
    public async Task The_engine_unreachable_reply_blocks_the_call()
    {
        var (_, cli) = await StartAsync(Simple);

        Assert.True(cli.Environment.TryGetValue(SandboxedAgentRuntime.UnreachableVariable, out var text), $"the CLI's environment has no {SandboxedAgentRuntime.UnreachableVariable}");
        var reply = JsonSerializer.Deserialize<HookReply>(text!, JsonSerializerOptions.Web)!;
        Assert.True(ReadReply(reply).Blocked, $"the CLI does not block a call on the unreachable reply {text}");
    }

    [Fact]
    public async Task A_session_is_started_polled_and_read_when_it_ends()
    {
        var script = new CliScript([new CliAction(ToolClass.Read, "/ff/work/a.txt"), new CliAction(ToolClass.Shell, Command: "ls"),
            new CliAction(ToolClass.Edit, "/ff/work/b.txt")], "Finished the work.", 120, 45);
        var (session, _) = await StartAsync(script);

        var progress = Ok(await Runtime.GetProgressAsync(session, Ct));
        var result = Ok(await Runtime.GetResultAsync(session, Ct));

        Assert.Equal((true, 1), (progress.Finished, progress.Turns));
        // Each tool call started and finished, then the last text, numbered in order; the stand-in's tools by their own names.
        Assert.Equal([ActivityKinds.ToolStarted, ActivityKinds.ToolFinished, ActivityKinds.ToolStarted, ActivityKinds.ToolFinished,
            ActivityKinds.ToolStarted, ActivityKinds.ToolFinished, ActivityKinds.Message], progress.Activity.Select(e => e.Kind));
        Assert.Equal(Enumerable.Range(1, 7), progress.Activity.Select(e => e.Sequence));
        Assert.All(progress.Activity.Where(e => e.Kind != ActivityKinds.Message), e => Assert.False(string.IsNullOrEmpty(e.CallId) || string.IsNullOrEmpty(e.Name)));
        Assert.Equal(("Finished the work.", false), (progress.Activity[^1].Text, progress.Activity.Any(e => e.Failed)));
        Assert.Equal((SessionEnd.Completed, "Finished the work."), (result.End, result.Detail));
        Assert.Equal((120L, 45L), (result.InputTokens, result.OutputTokens));
        Assert.Equal(["/ff/work/a.txt", "/ff/work/b.txt"], result.FilesOpened!.Order(StringComparer.Ordinal));
        Assert.NotNull(result.Transcript);
        Assert.Null(result.Report);
    }

    [Fact]
    public async Task A_continuation_resumes_the_same_session_and_another_step_has_its_own()
    {
        var (_, first) = await StartAsync(Simple);
        var (_, second) = await StartAsync(Simple, StepTask(session: 2), "Fix the report.");
        var (_, other) = await StartAsync(Simple, StepTask(key: "run-1/review/1"));

        var (id, resumes) = Session(first);
        Assert.False(resumes);
        Assert.NotNull(id);
        Assert.Equal((id, true), Session(second));
        Assert.Equal("Fix the report.", second.Files[second.Stdin!]);
        Assert.NotEqual(id, Session(other).Id);
    }

    /// <summary>
    /// A new worker finds a running session again by the step's key (R18), reading its output from the start with its
    /// activity numbered as the first worker read it, so recording it again adds nothing; stopping it cancels the command.
    /// </summary>
    [Fact]
    public async Task A_running_session_is_found_again_by_a_new_worker_and_stopped()
    {
        var (first, _) = await StartAsync(Simple with { Running = true });
        var read = Ok(await Runtime.GetProgressAsync(first, Ct)).Activity;
        var again = CreateRuntime(sandboxes, store);

        var session = Ok(await again.AttachAsync(sandbox, StepTask(), Ct));
        var progress = Ok(await again.GetProgressAsync(session, Ct));
        Assert.False(progress.Finished);
        Assert.NotEmpty(read);
        Assert.Equal(read, progress.Activity);
        Ok(await again.StopAsync(session, Ct));
        Assert.Contains(session.Id, sandboxes.Cancelled);
        Refused(await again.AttachAsync(sandbox, StepTask(session: 9), Ct), "not_found");
    }

    /// <summary>LOG 98: no status, 429 and 5xx are transient; 401 and 403 a configuration fault; any other 4xx a failure.</summary>
    [Theory]
    [InlineData(null, SessionEnd.ModelUnreachable)]
    [InlineData(429, SessionEnd.ModelUnreachable)]
    [InlineData(529, SessionEnd.ModelUnreachable)]
    [InlineData(401, SessionEnd.ModelRefused)]
    [InlineData(400, SessionEnd.Failed)]
    public async Task A_model_api_error_ends_the_session_as_its_status_says(int? status, string end)
    {
        var (session, _) = await StartAsync(Simple with { ApiError = "the model said no", ApiStatus = status, Exit = 1 });

        var result = Ok(await Runtime.GetResultAsync(session, Ct));
        Assert.Equal(end, result.End);
        Assert.Contains("the model said no", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_engine_unreachable_marker_a_lost_process_and_a_failed_exit_end_the_session_so()
    {
        var (unreachable, _) = await StartAsync(Simple);
        sandboxes.Put(SandboxedAgentRuntime.StateFolder + "/engine_unreachable", "no replica answered");
        Assert.Equal(SessionEnd.EngineUnreachable, Ok(await Runtime.GetResultAsync(unreachable, Ct)).End);

        var (lost, _) = await StartAsync(Simple with { Lost = true }, StepTask(session: 2));
        Assert.Equal(SessionEnd.Lost, Ok(await Runtime.GetResultAsync(lost, Ct)).End);

        var (failed, _) = await StartAsync(Simple with { Exit = 3 }, StepTask(session: 3));
        Assert.Equal(SessionEnd.Failed, Ok(await Runtime.GetResultAsync(failed, Ct)).End);
    }

    static JsonElement Element(JsonObject node) => JsonDocument.Parse(node.ToJsonString()).RootElement;

    /// <summary>The words of a <c>sh -c</c> command line the runtime built: single-quoted words, an optional <c>exec</c>, and <c>&lt; file</c>.</summary>
    public static (IReadOnlyList<string> Arguments, string? Stdin) Words(string line)
    {
        var words = new List<string>();
        string? stdin = null;
        var i = 0;
        var redirect = false;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }

            if (line[i] == '<')
            {
                redirect = true;
                i++;
                continue;
            }

            var word = new StringBuilder();
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                if (line[i] == '\'')
                {
                    var end = line.IndexOf('\'', i + 1);
                    word.Append(line, i + 1, end - i - 1);
                    i = end + 1;
                }
                else if (line[i] == '"')
                {
                    var end = line.IndexOf('"', i + 1);
                    word.Append(line, i + 1, end - i - 1);
                    i = end + 1;
                }
                else
                    word.Append(line[i++]);
            }

            if (redirect)
                stdin = word.ToString();
            else
                words.Add(word.ToString());
            redirect = false;
        }

        if (words.FirstOrDefault() == "exec")
            words.RemoveAt(0);
        return (words, stdin);
    }

    /// <summary>
    /// A sandbox for the suite: files in memory with whether they were written read-only, the runtime's CLI command run by
    /// the stand-in, and its own short commands (<c>cat</c>, <c>rm -f</c>, folder set-up) interpreted.
    /// </summary>
    sealed class ScriptedCliSandbox(MemoryArtifacts store) : ISandboxProvider
    {
        sealed record Command(string Output, int Exit, bool Running, bool Lost);

        readonly ConcurrentDictionary<string, (string Content, bool ReadOnly)> files = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<string, bool> copied = new(StringComparer.Ordinal);
        readonly ConcurrentDictionary<string, Command> commands = new(StringComparer.Ordinal);

        public Func<CliInvocation, (CliRun Run, CliScript Script)>? Cli { get; set; }

        public List<string> Cancelled { get; } = [];

        /// <summary>The files the runtime copied in, and whether read-only.</summary>
        public IEnumerable<(string Path, bool ReadOnly)> Written => copied.Select(f => (f.Key, f.Value));

        public void Put(string path, string content) => files[path] = (content, false);

        public ProviderDescription Describe() => new("sandbox", "contract", "0", new SandboxCapabilities(["linux"], false, false, true, false));

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Done(ProviderResult<Unit>.Success(default));

        public Task<ProviderResult<SandboxHandle>> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken) =>
            Done(ProviderResult<SandboxHandle>.Success(new SandboxHandle(spec.Key, "contract")));

        public Task<ProviderResult<SandboxHandle>> AttachAsync(string key, CancellationToken cancellationToken) =>
            Done(ProviderResult<SandboxHandle>.Success(new SandboxHandle(key, "contract")));

        public Task<ProviderResult<CommandHandle>> StartCommandAsync(SandboxHandle sandbox, SandboxCommand command, string key, CancellationToken cancellationToken)
        {
            var script = command.Arguments.Count == 3 && command.Arguments[0] == "sh" && command.Arguments[1] == "-c" ? command.Arguments[2] : string.Join(" ", command.Arguments);
            if (script.StartsWith("exec ", StringComparison.Ordinal))
            {
                var (arguments, stdin) = Words(script);
                var invocation = new CliInvocation(arguments, stdin, command.Environment ?? new Dictionary<string, string>(), command.WorkingDirectory,
                    files.ToDictionary(f => f.Key, f => f.Value.Content, StringComparer.Ordinal));
                var (run, played) = (Cli ?? throw new InvalidOperationException("no CLI stand-in"))(invocation);
                foreach (var (path, content) in run.Files ?? new Dictionary<string, string>())
                    files[path] = (content, false);
                commands[key] = new Command(run.Output, played.Exit, played.Running, played.Lost);
            }
            else
                commands[key] = new Command(Interpret(script), 0, false, false);

            return Done(ProviderResult<CommandHandle>.Success(new CommandHandle(sandbox, key)));
        }

        string Interpret(string script)
        {
            var output = new StringBuilder();
            foreach (Match m in Regex.Matches(script, @"\b(cat|rm -f)\s+(/[^\s;]+)"))
                if (m.Groups[1].Value == "cat" && files.TryGetValue(m.Groups[2].Value, out var file))
                    output.Append(file.Content);
                else if (m.Groups[1].Value == "rm -f")
                    files.TryRemove(m.Groups[2].Value, out _);
            return output.ToString();
        }

        public Task<ProviderResult<CommandHandle>> AttachCommandAsync(SandboxHandle sandbox, string key, CancellationToken cancellationToken) =>
            Done(commands.TryGetValue(key, out var c) && c.Running
                ? ProviderResult<CommandHandle>.Success(new CommandHandle(sandbox, key))
                : ProviderResult<CommandHandle>.Failure("not_found", $"no running command {key}"));

        public Task<ProviderResult<CommandStatus>> GetCommandStatusAsync(CommandHandle command, long cursor, CancellationToken cancellationToken)
        {
            if (!commands.TryGetValue(command.Key, out var c) || c.Lost)
                return Done(ProviderResult<CommandStatus>.Success(new CommandStatus(false, null, "", cursor, Lost: true)));
            var output = cursor >= c.Output.Length ? "" : c.Output[(int)cursor..];
            return Done(ProviderResult<CommandStatus>.Success(new CommandStatus(c.Running, c.Running ? null : c.Exit, output, c.Output.Length)));
        }

        public Task<ProviderResult<Unit>> CancelCommandAsync(CommandHandle command, CancellationToken cancellationToken)
        {
            lock (Cancelled)
                Cancelled.Add(command.Key);
            if (commands.TryGetValue(command.Key, out var c))
                commands[command.Key] = c with { Running = false, Exit = 143 };
            return Done(ProviderResult<Unit>.Success(default));
        }

        public async Task<ProviderResult<Unit>> CopyInAsync(SandboxHandle sandbox, ArtifactRef source, string path, bool readOnly, CancellationToken cancellationToken)
        {
            var got = await store.GetAsync(source, cancellationToken);
            if (!got.Ok)
                return new ProviderResult<Unit>(default, got.Error);
            await using var stream = got.Value!;
            files[path] = (await new StreamReader(stream).ReadToEndAsync(cancellationToken), readOnly);
            copied[path] = readOnly;
            return ProviderResult<Unit>.Success(default);
        }

        public Task<ProviderResult<ArtifactRef>> CopyOutAsync(SandboxHandle sandbox, string path, CancellationToken cancellationToken) =>
            Done(ProviderResult<ArtifactRef>.Failure("capability_missing", "not in the contract's sandbox"));

        public Task<ProviderResult<Unit>> MakeReadOnlyAsync(SandboxHandle sandbox, string path, CancellationToken cancellationToken) => Done(ProviderResult<Unit>.Success(default));

        public Task<ProviderResult<Unit>> DisposeAsync(SandboxHandle sandbox, CancellationToken cancellationToken) => Done(ProviderResult<Unit>.Success(default));

        static Task<T> Done<T>(T value) => System.Threading.Tasks.Task.FromResult(value);
    }

    sealed class MemoryArtifacts : IArtifactStoreProvider
    {
        readonly ConcurrentDictionary<string, byte[]> blobs = new(StringComparer.Ordinal);

        public ProviderDescription Describe() => new("artifact_store", "contract", "0", new object());

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => System.Threading.Tasks.Task.FromResult(ProviderResult<Unit>.Success(default));

        public async Task<ProviderResult<ArtifactRef>> PutAsync(Stream content, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var id = Guid.NewGuid().ToString("N");
            blobs[id] = buffer.ToArray();
            return ProviderResult<ArtifactRef>.Success(new ArtifactRef(id));
        }

        public Task<ProviderResult<Stream>> GetAsync(ArtifactRef reference, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(blobs.TryGetValue(reference.Id, out var bytes)
                ? ProviderResult<Stream>.Success(new MemoryStream(bytes))
                : ProviderResult<Stream>.Failure("not_found", reference.Id));

        public Task<ProviderResult<Uri>> GetSignedUrlAsync(ArtifactRef reference, TimeSpan ttl, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(ProviderResult<Uri>.Failure("capability_missing", "no signed urls"));
    }
}
