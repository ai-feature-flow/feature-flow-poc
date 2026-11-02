namespace FeatureFlow.Providers.Abstractions;

/// <summary>A sandbox, created by the sandbox provider. M3's scripted runtime runs with <see cref="None"/>.</summary>
public sealed record SandboxHandle(string Id, string Provider)
{
    public static readonly SandboxHandle None = new("none", "none");
}

/// <summary>
/// What a sandbox is created with (docs/06 section 3.1). <see cref="Home"/> is the runtime's home directory,
/// which the engine creates with startup files it controls (DR19); <see cref="Environment"/> holds no credential
/// (F6). <see cref="ReadOnlyPaths"/> are made unwritable for the agent (a read-only checkout, docs/04 section 3.7).
/// </summary>
public sealed record SandboxSpec(
    string Key,
    string Home,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Folders);

/// <summary>A command to run in a sandbox: argument vector, extra environment, working directory, and whether it runs as the agent's user.</summary>
public sealed record SandboxCommand(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string>? Environment = null, string? WorkingDirectory = null,
    bool AsRoot = false);

public sealed record CommandHandle(SandboxHandle Sandbox, string Key);

/// <summary>A command's state: still running, or ended with an exit code; output after the cursor; or lost (the sandbox restarted).</summary>
public sealed record CommandStatus(bool Running, int? ExitCode, string Output, long Cursor, bool Lost = false);

/// <summary>
/// The sandbox provider (docs/06 section 3.1). Commands are started and polled, never awaited, so a long command
/// survives a worker restart: <see cref="AttachCommandAsync"/> finds it again by key (risk R18).
/// </summary>
public interface ISandboxProvider : IProvider
{
    Task<ProviderResult<SandboxHandle>> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken);

    /// <summary>Finds the sandbox created with this key; <c>not_found</c> when it is gone.</summary>
    Task<ProviderResult<SandboxHandle>> AttachAsync(string key, CancellationToken cancellationToken);

    Task<ProviderResult<CommandHandle>> StartCommandAsync(SandboxHandle sandbox, SandboxCommand command, string key, CancellationToken cancellationToken);

    Task<ProviderResult<CommandHandle>> AttachCommandAsync(SandboxHandle sandbox, string key, CancellationToken cancellationToken);

    Task<ProviderResult<CommandStatus>> GetCommandStatusAsync(CommandHandle command, long cursor, CancellationToken cancellationToken);

    Task<ProviderResult<Unit>> CancelCommandAsync(CommandHandle command, CancellationToken cancellationToken);

    /// <summary>Copies an artifact into the sandbox at <paramref name="path"/>, owned by the agent's user unless <paramref name="readOnly"/>.</summary>
    Task<ProviderResult<Unit>> CopyInAsync(SandboxHandle sandbox, ArtifactRef source, string path, bool readOnly, CancellationToken cancellationToken);

    /// <summary>Copies a file out of the sandbox into the artifact store.</summary>
    Task<ProviderResult<ArtifactRef>> CopyOutAsync(SandboxHandle sandbox, string path, CancellationToken cancellationToken);

    /// <summary>Makes a path and everything under it unwritable for the agent's user (a read-only checkout).</summary>
    Task<ProviderResult<Unit>> MakeReadOnlyAsync(SandboxHandle sandbox, string path, CancellationToken cancellationToken);

    Task<ProviderResult<Unit>> DisposeAsync(SandboxHandle sandbox, CancellationToken cancellationToken);
}

public static class SandboxCommands
{
    /// <summary>Runs a short command to its end by starting and polling it: for the engine's own commands (git, mkdir), never the agent.</summary>
    public static async Task<ProviderResult<(int Exit, string Output)>> RunAsync(this ISandboxProvider sandboxes, SandboxHandle sandbox, SandboxCommand command,
        string key, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var started = await sandboxes.StartCommandAsync(sandbox, command, key, cancellationToken);
        if (!started.Ok)
            return new(default, started.Error);
        var handle = started.Value!;
        var output = new System.Text.StringBuilder();
        long cursor = 0;
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(5));
        while (true)
        {
            var status = await sandboxes.GetCommandStatusAsync(handle, cursor, cancellationToken);
            if (!status.Ok)
                return new(default, status.Error);
            output.Append(status.Value!.Output);
            cursor = status.Value.Cursor;
            if (status.Value.Lost)
                return ProviderResult<(int, string)>.Failure("unreachable", $"the command {key} was lost");
            if (!status.Value.Running)
                return ProviderResult<(int, string)>.Success((status.Value.ExitCode ?? -1, output.ToString()));
            if (DateTimeOffset.UtcNow > deadline)
            {
                await sandboxes.CancelCommandAsync(handle, CancellationToken.None);
                return ProviderResult<(int, string)>.Failure("timeout", $"the command {key} did not finish in time");
            }

            await Task.Delay(100, cancellationToken);
        }
    }
}
