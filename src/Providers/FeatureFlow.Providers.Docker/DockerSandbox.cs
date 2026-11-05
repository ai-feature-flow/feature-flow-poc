using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Docker;

/// <summary>
/// The local Docker sandbox (docs/06 section 3.1; the solo profile). One container per step, named from the step's key,
/// so a new worker finds it again after a restart (docs/02 section 4.3). Commands and copies are
/// <see cref="ShellSandbox"/>'s, over <c>docker exec</c>.
/// <para>
/// The container's environment holds only what <see cref="SandboxSpec.Environment"/> gives, never a credential (F6). The
/// agent runs as an unprivileged user, so a path made read-only cannot be changed by it. The home directory is created
/// empty, with empty shell startup files (DR19). The container reaches the agent worker as <c>host.docker.internal</c>,
/// and its network is closed at creation to everything but <paramref name="reach"/> and the public internet
/// (<see cref="ShellSandbox.NetworkScript"/>): the container has <c>NET_ADMIN</c> for root's rules, and the agent's user,
/// with no capabilities, cannot change them (M4 follow-up 12).
/// </para>
/// </summary>
public sealed class DockerSandbox(string image, string user, Func<IArtifactStoreProvider> store, DockerCli? docker = null, IReadOnlyList<Uri>? reach = null)
    : ShellSandbox(user, store)
{
    public const string Label = "feature-flow.sandbox";
    readonly DockerCli cli = docker ?? new DockerCli();
    readonly IReadOnlyList<Uri> reach = reach ?? [];

    public override ProviderDescription Describe() => new("sandbox", "docker", "0.2.0", new SandboxCapabilities(["linux"], false, true, true, true));

    public override async Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken)
    {
        var run = await cli.RunAsync(["image", "inspect", image, "--format", "{{.Id}}"], cancellationToken);
        if (!run.Ok)
            return new ProviderResult<Unit>(default, run.Error);
        return run.Value!.Exit == 0
            ? ProviderResult<Unit>.Success(default)
            : ProviderResult<Unit>.Failure("not_found", $"the sandbox image {image} is not present: build it first");
    }

    public override async Task<ProviderResult<SandboxHandle>> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken)
    {
        var name = NameOf(spec.Key);
        if ((await AttachAsync(spec.Key, cancellationToken)) is { Ok: true } existing)
            return existing;
        var arguments = new List<string>
        {
            "create", "--name", name, "--label", $"{Label}=1", "--label", $"{Label}.key={spec.Key}",
            "--add-host", "host.docker.internal:host-gateway", "--cap-add", "NET_ADMIN", "--init", "-e", $"HOME={spec.Home}",
        };
        foreach (var (key, value) in spec.Environment)
            arguments.AddRange(["-e", $"{key}={value}"]);
        arguments.AddRange([image, "sleep", "infinity"]);
        if (await Docker(arguments, "create", cancellationToken) is { } created)
            return created.Convert<SandboxHandle>();
        if (await Docker(["start", name], "start", cancellationToken) is { } started)
            return started.Convert<SandboxHandle>();
        var handle = new SandboxHandle(name, "docker");
        // The network is closed before anything else runs in the container; a failure removes it rather than leave it open.
        if (await Check(ExecAsync(handle, NetworkScript(reach), null, cancellationToken), "close its network") is { } open)
        {
            await DisposeAsync(handle, CancellationToken.None);
            return open.Convert<SandboxHandle>();
        }

        if (await PrepareAsync(handle, spec, cancellationToken) is { } failed)
            return failed.Convert<SandboxHandle>();
        return ProviderResult<SandboxHandle>.Success(handle);
    }

    public override async Task<ProviderResult<SandboxHandle>> AttachAsync(string key, CancellationToken cancellationToken)
    {
        var name = NameOf(key);
        var inspect = await cli.RunAsync(["inspect", "--format", "{{.State.Running}}", name], cancellationToken);
        if (!inspect.Ok)
            return new ProviderResult<SandboxHandle>(null, inspect.Error);
        if (inspect.Value!.Exit != 0)
            return ProviderResult<SandboxHandle>.Failure("not_found", $"no sandbox for {key}");
        if (inspect.Value.Text.Trim() != "true")
        {
            // A restarted container starts with no rules: close its network again before it is used.
            var handle = new SandboxHandle(name, "docker");
            if (await Docker(["start", name], "start", cancellationToken) is { } failed)
                return failed.Convert<SandboxHandle>();
            if (await Check(ExecAsync(handle, NetworkScript(reach), null, cancellationToken), "close its network") is { } open)
                return open.Convert<SandboxHandle>();
        }

        return ProviderResult<SandboxHandle>.Success(new SandboxHandle(name, "docker"));
    }

    public override async Task<ProviderResult<Unit>> DisposeAsync(SandboxHandle sandbox, CancellationToken cancellationToken)
    {
        var run = await cli.RunAsync(["rm", "-f", sandbox.Id], cancellationToken);
        return run.Ok ? ProviderResult<Unit>.Success(default) : new ProviderResult<Unit>(default, run.Error);
    }

    protected override async Task<ProviderResult<ShellRun>> ExecAsync(SandboxHandle sandbox, string script, Stream? input, CancellationToken cancellationToken)
    {
        List<string> arguments = input is null ? ["exec", "-u", "root", sandbox.Id, "sh", "-c", script] : ["exec", "-i", "-u", "root", sandbox.Id, "sh", "-c", script];
        var run = await cli.RunAsync(arguments, cancellationToken, input);
        return run.Ok ? ProviderResult<ShellRun>.Success(new ShellRun(run.Value!.Exit, run.Value.Output, run.Value.Error)) : new ProviderResult<ShellRun>(null, run.Error);
    }

    async Task<ProviderResult<Unit>?> Docker(IReadOnlyList<string> arguments, string what, CancellationToken cancellationToken)
    {
        var run = await cli.RunAsync(arguments, cancellationToken);
        if (!run.Ok)
            return new ProviderResult<Unit>(default, run.Error);
        return run.Value!.Exit == 0 ? null : ProviderResult<Unit>.Failure("server_error", $"docker could not {what}: {run.Value.Error.Trim()}");
    }
}
