using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FeatureFlow.Providers.Abstractions;

/// <summary>
/// A tool call a hook reported, in the engine's terms (LOG 65): the runtime's tool name, its abstract tool class
/// (<see cref="ToolClass"/>), the file it names and the command it runs. A null class is a tool the runtime does not map.
/// </summary>
public sealed record ToolCall(string Tool, string? Class, string? Path = null, string? Command = null);

/// <summary>The abstract tool classes a role lists (LOG 65), and <see cref="Engine"/> for the engine's own MCP tools.</summary>
public static class ToolClass
{
    public const string Read = "read";
    public const string Search = "search";
    public const string Edit = "edit";
    public const string Shell = "shell";
    public const string Engine = "engine";
}

/// <summary>What the hook command prints and exits with: the runtime's own way of refusing a tool call.</summary>
public sealed record HookReply(int Exit, string? Output = null, string? Error = null);

/// <summary>
/// A runtime's side of the hooks (docs/02 section 4.1): it reads its CLI's hook input as a <see cref="ToolCall"/>, so
/// the engine's policy sees tool classes only, and says how its CLI is told a call is refused (LOG 444).
/// </summary>
public interface IAgentHooks
{
    ToolCall Classify(JsonElement hookInput);

    HookReply Deny(string reason);
}

/// <summary>
/// Settings of a sandboxed runtime instance (docs/07 section 2): <see cref="Model"/> and <see cref="Executable"/>.
/// <see cref="HookHold"/> and <see cref="ClientRetry"/> are fixed defaults, not settings (LOG 91); only tests change them.
/// </summary>
public record AgentRuntimeOptions(string Model, string Executable, TimeSpan? HookHold = null, TimeSpan? ClientRetry = null)
{
    /// <summary>How long the hook command holds a call while no replica answers, before it stops the session (R19).</summary>
    public TimeSpan Hold => HookHold ?? TimeSpan.FromSeconds(120);

    /// <summary>How long the sandbox client retries an engine tool call before it ends the session (LOG 60).</summary>
    public TimeSpan Retry => ClientRetry ?? TimeSpan.FromMinutes(10);
}

/// <summary>What a runtime read from a session's output: its last text, an API error and status, usage, and the files its tools opened.</summary>
public sealed record Transcript(string? Result, string? ApiError, long InputTokens, long OutputTokens, IReadOnlyList<string> Files, int? ApiStatus = null);

/// <summary>
/// An agent runtime whose CLI runs in a sandbox (docs/06 section 3.2), with the session lifecycle every such runtime
/// shares: a session is the CLI started by key in the step's sandbox from files the engine writes read-only, found again
/// by that key, polled for its activity as its output grows, read when it ends and stopped by cancelling it. A runtime supplies its command line,
/// environment, files, transcript parser, output format, hook table and hook reply (F12).
/// <para>
/// The runtime is given only the step's token and the model proxy's URL, never a model credential (F6, SP5). Reporting
/// goes through the engine's MCP tools (SP3), served by the sandbox client; hooks call the hook endpoint through it.
/// </para>
/// </summary>
public abstract class SandboxedAgentRuntime(AgentRuntimeOptions options, Func<ISandboxProvider> sandboxes, Func<IArtifactStoreProvider> store)
    : IAgentRuntimeProvider, IAgentHooks
{
    public const string AgentFolder = "/ff/agent";
    public const string StateFolder = "/ff/state";

    /// <summary>The sandbox client's hook command for an event: the same for every runtime (LOG 444).</summary>
    public static string HookCommand(string hookEvent) => $"/ff/bin/ff-sandbox hook {hookEvent}";

    /// <summary>
    /// The variable that gives the hook command the runtime's reply when no replica answers (<see cref="Unreachable"/>),
    /// as JSON in the endpoint's shape (<c>exit</c>, <c>output</c>, <c>error</c>).
    /// </summary>
    public const string UnreachableVariable = "FF_HOOK_UNREACHABLE";

    /// <summary>Each session's sandbox, its output read so far (a byte cursor, and the reader holding any partial last line), its messages, and its number.</summary>
    readonly ConcurrentDictionary<string, (SandboxHandle Sandbox, long Cursor, TranscriptReader Reader, int Turns, int Session)> progress = new();

    protected AgentRuntimeOptions Options => options;

    /// <summary>The sandbox provider, for a runtime's own commands.</summary>
    protected ISandboxProvider Sandboxes => sandboxes();

    /// <summary>The provider kind, as configured (<c>claude_code</c>).</summary>
    protected abstract string Kind { get; }

    /// <summary>The runtime's tools for each abstract tool class, as offered on its command line (LOG 65).</summary>
    public abstract IReadOnlyDictionary<string, IReadOnlyList<string>> ToolClasses { get; }

    /// <summary>
    /// The tool names its hooks report that the command line does not offer by that name, each with the offered tool it
    /// comes with or stands for, whose class it has: a background shell's output with <c>Bash</c>, or another name for
    /// the same tool. None by default.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string> CompanionTools { get; } = new Dictionary<string, string>();

    IReadOnlyDictionary<string, string>? hookTools;

    /// <summary>Every tool name the runtime's hooks report, with its class: the offered tools and their companions, one table.</summary>
    IReadOnlyDictionary<string, string> HookTools => hookTools ??= MergeHookTools();

    IReadOnlyDictionary<string, string> MergeHookTools()
    {
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (toolClass, tools) in ToolClasses)
            foreach (var tool in tools)
                if (!classes.TryAdd(tool, toolClass))
                    throw new InvalidOperationException($"{Kind} offers the tool {tool} in two classes");
        foreach (var (tool, offered) in CompanionTools)
            if (!classes.TryAdd(tool, classes.GetValueOrDefault(offered) ?? throw new InvalidOperationException($"{Kind}'s tool {tool} comes with {offered}, which it does not offer")))
                throw new InvalidOperationException($"{Kind}'s companion tool {tool} is also offered");
        return classes;
    }

    /// <summary>The fields of a hook's tool input that name a file, in order.</summary>
    protected abstract IReadOnlyList<string> PathFields { get; }

    /// <summary>The prefix of the engine's MCP tools in the runtime's hook input.</summary>
    protected abstract string EngineToolPrefix { get; }

    /// <summary>The format of the CLI's output, which the shared reader reads as it grows (<see cref="AgentTranscripts"/>).</summary>
    protected abstract TranscriptFormat Format { get; }

    /// <summary>The command line, reading the prompt from a file so no task text sits in an argument.</summary>
    public abstract string CommandLine(AgentTask task, string promptFile, bool resume);

    /// <summary>The runtime's own variables, added to the sandbox client's.</summary>
    protected abstract IReadOnlyDictionary<string, string> RuntimeEnvironment(AgentTask task, AgentLimits limits, EngineEndpoints engine, AgentPaths paths);

    /// <summary>The settings files the runtime reads, written read-only before each session; the prompt file is added.</summary>
    protected abstract IEnumerable<(string Path, string Content)> Files(AgentTask task, AgentLimits limits);

    /// <summary>Reads a session's output, and anything else the runtime wrote in the sandbox for it.</summary>
    protected abstract Task<Transcript> ReadAsync(SandboxHandle sandbox, string output, int session, string key, CancellationToken cancellationToken);

    public abstract HookReply Deny(string reason);

    /// <summary>
    /// What the hook command prints and exits with when no replica answers for the hold (R19): the CLI's own way of
    /// stopping the session or, failing that, blocking the call, without telling the agent of an engine error (LOG 60).
    /// </summary>
    protected abstract HookReply Unreachable { get; }

    public ProviderDescription Describe() => new("agent_runtime", Kind, "0.1.0", new AgentRuntimeCapabilities(true, true, true, true, true, true, true, null));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    /// <summary>A session id fixed by <paramref name="seed"/>, the step's key, so a continuation resumes the same conversation.</summary>
    protected static string StableSessionId(string seed)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    /// <summary>DR16: the engine MCP server's tool timeout exceeds the longest a question may wait.</summary>
    public static TimeSpan McpToolTimeout(AgentLimits limits) => (limits.QuestionsTimeout ?? AgentLimits.DefaultQuestionsTimeout) + TimeSpan.FromMinutes(15);

    /// <summary>DR16: the hook command's timeout exceeds the client's hold.</summary>
    public TimeSpan HookTimeout => options.Hold + TimeSpan.FromSeconds(60);

    /// <summary>The role's tools; a role with no write access gets no file-editing tool (its checkout is read-only anyway).</summary>
    public List<string> Tools(AgentTask task) => task.Tools.SelectMany(c => ToolClasses.GetValueOrDefault(c, []))
        .Where(t => task.WriteAccess != "none" || !ToolClasses[ToolClass.Edit].Contains(t)).Distinct().ToList();

    /// <summary>
    /// The environment of the runtime and of the sandbox client it starts. It holds the step's token and the proxy's URL,
    /// never a model credential (F6). <c>HOME</c> is the engine's (DR19).
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment(AgentTask task, AgentLimits limits)
    {
        var engine = task.Engine ?? throw new InvalidOperationException("an agent task in a sandbox needs the engine's endpoints");
        var paths = task.Paths ?? throw new InvalidOperationException("an agent task in a sandbox needs its paths");
        var env = new Dictionary<string, string>
        {
            ["HOME"] = paths.Home,
            ["FF_ENGINE_URLS"] = string.Join(",", engine.AgentUrls),
            ["FF_STEP_TOKEN"] = engine.StepToken,
            ["FF_OUTPUT"] = paths.Output,
            ["FF_CHECKOUT"] = paths.Checkout,
            ["FF_STATE"] = StateFolder,
            ["FF_CLIENT_LOG"] = StateFolder + "/client.jsonl",
            ["FF_QUESTIONS"] = task.Questions ? "true" : "false",
            ["FF_WRITE"] = task.WriteAccess == "none" ? "false" : "true",
            ["FF_PLANNING"] = task.Planning is not null ? "true" : "false",
            ["FF_HOOK_HOLD_SECONDS"] = ((int)options.Hold.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["FF_RETRY_SECONDS"] = ((int)options.Retry.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            [UnreachableVariable] = JsonSerializer.Serialize(Unreachable, JsonSerializerOptions.Web),
        };
        foreach (var (name, value) in RuntimeEnvironment(task, limits, engine, paths))
            env[name] = value;
        return env;
    }

    /// <summary>The sandbox client's variables, for the engine MCP server's entry in the runtime's MCP configuration.</summary>
    protected JsonObject ClientEnvironment(AgentTask task, AgentLimits limits) =>
        new(Environment(task, limits).Where(kv => kv.Key.StartsWith("FF_", StringComparison.Ordinal))
            .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));

    static string CommandKey(AgentTask task) => $"{task.Step.IdempotencyKey}/session-{task.Session}";

    public Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken) =>
        LaunchAsync(sandbox, task, limits, task.TaskText ?? "", resume: false, cancellationToken);

    public Task<ProviderResult<AgentSession>> ContinueAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, string message, CancellationToken cancellationToken) =>
        LaunchAsync(sandbox, task, limits, message, resume: true, cancellationToken);

    /// <summary>
    /// Anything the sandbox needs before the files are written (a folder only root may own), for a new session or, if
    /// <paramref name="resume"/>, a continuation of the step's session. None by default.
    /// </summary>
    protected virtual Task<ProviderResult<Unit>> PrepareAsync(ISandboxProvider sandboxes, SandboxHandle sandbox, AgentTask task, bool resume, string key,
        CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult<Unit>.Success(default));

    async Task<ProviderResult<AgentSession>> LaunchAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, string prompt, bool resume,
        CancellationToken cancellationToken)
    {
        var provider = sandboxes();
        var key = CommandKey(task);
        if ((await provider.AttachCommandAsync(sandbox, key, cancellationToken)) is { Ok: true })
            return Session(sandbox, key, task.Session);
        var prepared = await PrepareAsync(provider, sandbox, task, resume, key, cancellationToken);
        if (!prepared.Ok)
            return new ProviderResult<AgentSession>(null, prepared.Error);

        var promptFile = $"{AgentFolder}/prompt-{task.Session}.md";
        foreach (var (path, content) in Files(task, limits).Append((promptFile, prompt)))
        {
            var put = await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), cancellationToken);
            if (!put.Ok)
                return new ProviderResult<AgentSession>(null, put.Error);
            var copied = await provider.CopyInAsync(sandbox, put.Value!, path, readOnly: true, cancellationToken);
            if (!copied.Ok)
                return new ProviderResult<AgentSession>(null, copied.Error);
        }

        var command = new SandboxCommand(["sh", "-c", CommandLine(task, promptFile, resume)], Environment(task, limits), task.Paths!.Checkout);
        var started = await provider.StartCommandAsync(sandbox, command, key, cancellationToken);
        return started.Ok ? Session(sandbox, key, task.Session) : new ProviderResult<AgentSession>(null, started.Error);
    }

    /// <summary>
    /// Registers a session at the start of its output. A new worker re-attaching reads the whole output again: its entries
    /// are numbered as before (<see cref="ActivityEntry.Sequence"/>), so recording them again adds nothing.
    /// </summary>
    ProviderResult<AgentSession> Session(SandboxHandle sandbox, string key, int session)
    {
        progress.TryAdd(key, (sandbox, 0, AgentTranscripts.Reader(Format), 0, session));
        return ProviderResult<AgentSession>.Success(new AgentSession(key, Kind));
    }

    public async Task<ProviderResult<AgentSession>> AttachAsync(SandboxHandle sandbox, AgentTask task, CancellationToken cancellationToken)
    {
        var key = CommandKey(task);
        var found = await sandboxes().AttachCommandAsync(sandbox, key, cancellationToken);
        return found.Ok ? Session(sandbox, key, task.Session) : new ProviderResult<AgentSession>(null, found.Error);
    }

    public Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult<AgentSession>.Failure("capability_missing", "resume with a message: ContinueAsync"));

    /// <summary>Reads the output written since the last call: the activity entries its complete lines hold, and whether the session has finished.</summary>
    public async Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (!progress.TryGetValue(session.Id, out var state))
            return ProviderResult<AgentProgress>.Failure("not_found", $"no session {session.Id}");
        var status = await sandboxes().GetCommandStatusAsync(new CommandHandle(state.Sandbox, session.Id), state.Cursor, cancellationToken);
        if (!status.Ok)
            return new ProviderResult<AgentProgress>(null, status.Error);
        var activity = state.Reader.Feed(status.Value!.Output);
        var turns = state.Turns + activity.Count(e => e.Kind == ActivityKinds.Message);
        progress[session.Id] = state with { Cursor = status.Value.Cursor, Turns = turns };
        return ProviderResult<AgentProgress>.Success(new AgentProgress(!status.Value.Running, turns) { Activity = activity });
    }

    public async Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (!progress.TryGetValue(session.Id, out var state))
            return ProviderResult<AgentResult>.Failure("not_found", $"no session {session.Id}");
        var provider = sandboxes();
        var status = await provider.GetCommandStatusAsync(new CommandHandle(state.Sandbox, session.Id), 0, cancellationToken);
        if (!status.Ok)
            return new ProviderResult<AgentResult>(null, status.Error);
        var output = status.Value!.Output;
        var put = await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(output)), cancellationToken);
        var marker = await provider.RunAsync(state.Sandbox, new SandboxCommand(["sh", "-c", $"cat {StateFolder}/engine_unreachable 2>/dev/null; rm -f {StateFolder}/engine_unreachable"], AsRoot: true),
            $"{session.Id}/marker/{Guid.NewGuid():N}", cancellationToken);
        var parsed = await ReadAsync(state.Sandbox, output, state.Session, session.Id, cancellationToken);
        var end = marker.Ok && marker.Value.Output.Trim().Length > 0 ? SessionEnd.EngineUnreachable
            : parsed.ApiError is not null ? ApiEnd(parsed.ApiStatus)
            : status.Value.Lost ? SessionEnd.Lost
            : status.Value.ExitCode == 0 ? SessionEnd.Completed
            : SessionEnd.Failed;
        var detail = end switch
        {
            SessionEnd.EngineUnreachable => "the sandbox client could not reach the engine",
            SessionEnd.Lost => "the session's process was lost",
            SessionEnd.ModelRefused => $"the model answered {parsed.ApiStatus}: {parsed.ApiError}",
            SessionEnd.ModelUnreachable => "the model could not be reached: " + parsed.ApiError,
            SessionEnd.Failed => parsed.ApiError is not null ? $"the model answered {parsed.ApiStatus}: {parsed.ApiError}" : $"{Path.GetFileName(options.Executable)} exited {status.Value.ExitCode}: {parsed.Result}",
            _ => parsed.Result,
        };
        return ProviderResult<AgentResult>.Success(new AgentResult(null, end, detail, parsed.InputTokens, parsed.OutputTokens, parsed.Files, put.Ok ? put.Value!.Id : null)
        {
            QuietWindow = options.Hold < options.Retry ? options.Hold : options.Retry,
        });
    }

    public async Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (!progress.TryGetValue(session.Id, out var state))
            return ProviderResult<Unit>.Success(default);
        return await sandboxes().CancelCommandAsync(new CommandHandle(state.Sandbox, session.Id), cancellationToken);
    }

    /// <summary>
    /// How a model API error ends the session (LOG 98): no status (the connection failed), 429 and 5xx are transient,
    /// so resumable; 401 and 403 are a configuration fault; any other 4xx (a request too large, a prompt too long) is
    /// permanent, a failed session.
    /// </summary>
    public static string ApiEnd(int? status) => status switch
    {
        null or 429 or >= 500 => SessionEnd.ModelUnreachable,
        401 or 403 => SessionEnd.ModelRefused,
        _ => SessionEnd.Failed,
    };

    /// <summary>Reads a hook's input, <c>tool_name</c> and <c>tool_input</c>, as a tool call in the engine's terms.</summary>
    public ToolCall Classify(JsonElement hookInput)
    {
        var tool = Text(hookInput, "tool_name") ?? "";
        var input = hookInput.ValueKind == JsonValueKind.Object && hookInput.TryGetProperty("tool_input", out var i) ? i : default;
        var toolClass = tool.StartsWith(EngineToolPrefix, StringComparison.Ordinal) ? ToolClass.Engine
            : HookTools.GetValueOrDefault(tool);
        return new ToolCall(tool, toolClass, PathFields.Select(f => Text(input, f)).FirstOrDefault(p => p is not null), Text(input, "command"));
    }

    static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    protected static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}
