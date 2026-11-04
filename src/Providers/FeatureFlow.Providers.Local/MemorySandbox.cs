using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>
/// What a scripted command does: the latest rule whose <see cref="Pattern"/> matches the command line (its arguments
/// joined by spaces) decides its exit code and output, the files it writes in the sandbox (paths relative to its
/// working directory, or absolute), and the edits it makes to an in-memory working copy there (null deletes a path).
/// </summary>
public sealed record SandboxRule(string Pattern, int Exit = 0, string Output = "", IReadOnlyDictionary<string, string>? Files = null,
    IReadOnlyDictionary<string, string?>? Edits = null);

/// <summary>
/// The <c>memory</c> sandbox provider (M5): a scripted sandbox for engine tests of <c>test.run</c>, <c>analysis.run</c>
/// and the manifest's commands, with no container. Each sandbox holds files in memory; commands do what the latest
/// matching <see cref="SandboxRule"/> says (else exit 0) and are recorded in <see cref="Commands"/>. With the
/// <c>memory</c> VCS, a checkout is the shared world's working copy, which a rule's <see cref="SandboxRule.Edits"/>
/// change as a formatter would. Sandboxes configured with the same <c>world</c> share one instance (<see cref="Named"/>).
/// </summary>
public sealed class MemorySandbox(Func<IArtifactStoreProvider> store, InMemoryForge? forge = null) : ISandboxProvider
{
    static readonly ConcurrentDictionary<string, MemorySandbox> Worlds = new(StringComparer.Ordinal);

    /// <summary>The sandbox provider configured with <c>world: &lt;name&gt;</c>, sharing <see cref="InMemoryForge.Named"/>'s world of that name.</summary>
    public static MemorySandbox Named(string name, Func<IArtifactStoreProvider> store) =>
        Worlds.GetOrAdd(name, n => new MemorySandbox(store, InMemoryForge.Named(n)));

    sealed class Box(string key)
    {
        public string Key { get; } = key;
        public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (int Exit, string Output)> Commands = new(StringComparer.Ordinal);
    }

    readonly object gate = new();
    readonly Dictionary<string, Box> boxes = new(StringComparer.Ordinal);
    readonly List<SandboxRule> rules = [];
    readonly List<(string Sandbox, string Command, string? Directory)> commands = [];
    int created;

    /// <summary>Adds a rule. It is tried before the rules added earlier, so a test can change what a command does.</summary>
    public MemorySandbox On(SandboxRule rule)
    {
        lock (gate)
            rules.Add(rule);
        return this;
    }

    /// <summary>Every command started, in order: the sandbox's key, the command line and its working directory.</summary>
    public IReadOnlyList<(string Sandbox, string Command, string? Directory)> Commands
    {
        get
        {
            lock (gate)
                return commands.ToList();
        }
    }

    /// <summary>How many sandboxes were created, and how many exist now.</summary>
    public (int Created, int Live) Counts
    {
        get
        {
            lock (gate)
                return (created, boxes.Count);
        }
    }

    /// <summary>Writes a file into a sandbox, as an agent would in its output folder.</summary>
    public void WriteFile(SandboxHandle sandbox, string path, string content)
    {
        lock (gate)
            boxes[sandbox.Id].Files[path] = content;
    }

    public string? ReadFile(SandboxHandle sandbox, string path)
    {
        lock (gate)
            return boxes.GetValueOrDefault(sandbox.Id)?.Files.GetValueOrDefault(path);
    }

    public ProviderDescription Describe() => new("sandbox", "memory", "0.1.0", new SandboxCapabilities(["linux"], false, false, true, false));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Done(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<SandboxHandle>> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (boxes.ContainsKey(spec.Key))
                return Done(ProviderResult<SandboxHandle>.Failure("validation_failed", $"a sandbox with key {spec.Key} exists"));
            boxes[spec.Key] = new Box(spec.Key);
            created++;
            return Done(ProviderResult<SandboxHandle>.Success(Handle(spec.Key)));
        }
    }

    public Task<ProviderResult<SandboxHandle>> AttachAsync(string key, CancellationToken cancellationToken)
    {
        lock (gate)
            return Done(boxes.ContainsKey(key)
                ? ProviderResult<SandboxHandle>.Success(Handle(key))
                : ProviderResult<SandboxHandle>.Failure("not_found", $"no sandbox {key}"));
    }

    public Task<ProviderResult<CommandHandle>> StartCommandAsync(SandboxHandle sandbox, SandboxCommand command, string key, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!boxes.TryGetValue(sandbox.Id, out var box))
                return Done(ProviderResult<CommandHandle>.Failure("not_found", $"no sandbox {sandbox.Id}"));
            if (!box.Commands.ContainsKey(key))
            {
                var line = string.Join(" ", command.Arguments);
                commands.Add((sandbox.Id, line, command.WorkingDirectory));
                var rule = rules.LastOrDefault(r => Regex.IsMatch(line, r.Pattern));
                foreach (var (path, content) in rule?.Files ?? new Dictionary<string, string>())
                    box.Files[Absolute(path, command.WorkingDirectory)] = content;
                if (rule?.Edits is { } edits && forge?.WorkingFiles(new WorkingCopyRef(sandbox, command.WorkingDirectory ?? "/")) is { } files)
                    lock (forge.Gate)
                        foreach (var (path, content) in edits)
                            if (content is null)
                                files.Remove(path);
                            else
                                files[path] = content;
                box.Commands[key] = (rule?.Exit ?? 0, rule?.Output ?? "");
            }

            return Done(ProviderResult<CommandHandle>.Success(new CommandHandle(sandbox, key)));
        }
    }

    public Task<ProviderResult<CommandHandle>> AttachCommandAsync(SandboxHandle sandbox, string key, CancellationToken cancellationToken)
    {
        lock (gate)
            return Done(boxes.TryGetValue(sandbox.Id, out var box) && box.Commands.ContainsKey(key)
                ? ProviderResult<CommandHandle>.Success(new CommandHandle(sandbox, key))
                : ProviderResult<CommandHandle>.Failure("not_found", $"no command {key}"));
    }

    public Task<ProviderResult<CommandStatus>> GetCommandStatusAsync(CommandHandle command, long cursor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!boxes.TryGetValue(command.Sandbox.Id, out var box) || !box.Commands.TryGetValue(command.Key, out var result))
                return Done(ProviderResult<CommandStatus>.Success(new CommandStatus(false, null, "", cursor, Lost: true)));
            var output = cursor >= result.Output.Length ? "" : result.Output[(int)cursor..];
            return Done(ProviderResult<CommandStatus>.Success(new CommandStatus(false, result.Exit, output, result.Output.Length)));
        }
    }

    public Task<ProviderResult<Unit>> CancelCommandAsync(CommandHandle command, CancellationToken cancellationToken) => Done(ProviderResult<Unit>.Success(default));

    public async Task<ProviderResult<Unit>> CopyInAsync(SandboxHandle sandbox, ArtifactRef source, string path, bool readOnly, CancellationToken cancellationToken)
    {
        var got = await store().GetAsync(source, cancellationToken);
        if (!got.Ok)
            return new ProviderResult<Unit>(default, got.Error);
        string text;
        await using (var stream = got.Value!)
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            text = await reader.ReadToEndAsync(cancellationToken);
        lock (gate)
        {
            if (!boxes.TryGetValue(sandbox.Id, out var box))
                return ProviderResult<Unit>.Failure("not_found", $"no sandbox {sandbox.Id}");
            box.Files[path] = text;
            // A file copied into a checkout is a change to the in-memory working copy there, as on a real file system.
            if (forge is not null)
                lock (forge.Gate)
                    foreach (var (copy, working) in forge.WorkingCopies)
                        if (copy.Sandbox.Id == sandbox.Id && path.StartsWith(copy.Path.TrimEnd('/') + "/", StringComparison.Ordinal))
                            working.Files[path[(copy.Path.TrimEnd('/').Length + 1)..]] = text;
            return ProviderResult<Unit>.Success(default);
        }
    }

    public async Task<ProviderResult<ArtifactRef>> CopyOutAsync(SandboxHandle sandbox, string path, CancellationToken cancellationToken)
    {
        string? text;
        lock (gate)
            text = boxes.GetValueOrDefault(sandbox.Id)?.Files.GetValueOrDefault(path);
        if (text is null)
            return ProviderResult<ArtifactRef>.Failure("not_found", $"no file {path} in sandbox {sandbox.Id}");
        return await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), cancellationToken);
    }

    public Task<ProviderResult<Unit>> MakeReadOnlyAsync(SandboxHandle sandbox, string path, CancellationToken cancellationToken) =>
        Done(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<Unit>> DisposeAsync(SandboxHandle sandbox, CancellationToken cancellationToken)
    {
        lock (gate)
            boxes.Remove(sandbox.Id);
        return Done(ProviderResult<Unit>.Success(default));
    }

    static SandboxHandle Handle(string key) => new(key, "memory");

    static string Absolute(string path, string? directory) => path.StartsWith('/') ? path : $"{(directory ?? "").TrimEnd('/')}/{path}";

    static Task<T> Done<T>(T value) => Task.FromResult(value);
}
