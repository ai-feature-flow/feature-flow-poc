using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Docker;

/// <summary>
/// The local Docker sandbox (docs/06 section 3.1; the solo profile). One container per step, named from the
/// step's key, so a new worker finds it again after a restart (docs/02 section 4.3). Commands are started
/// detached in their own process group, with their output, process id and exit code in files under
/// <c>/ff/cmd/&lt;id&gt;</c>, so they are polled rather than awaited and survive the worker (risk R18).
/// <para>
/// The container's environment holds only what <see cref="SandboxSpec.Environment"/> gives, never a credential
/// (F6). The agent runs as an unprivileged user, so a path made read-only cannot be changed by it. The home
/// directory is created empty, with empty shell startup files (DR19). The container reaches the agent worker as
/// <c>host.docker.internal</c>.
/// </para>
/// </summary>
public sealed class DockerSandbox(string image, string user, Func<IArtifactStoreProvider> store, DockerCli? docker = null) : ISandboxProvider
{
    public const string Label = "feature-flow.sandbox";
    readonly DockerCli cli = docker ?? new DockerCli();

    public ProviderDescription Describe() => new("sandbox", "docker", "0.1.0", new SandboxCapabilities(["linux"], false, false, true, true));

    public async Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken)
    {
        var run = await cli.RunAsync(["image", "inspect", image, "--format", "{{.Id}}"], cancellationToken);
        if (!run.Ok)
            return new ProviderResult<Unit>(default, run.Error);
        return run.Value!.Exit == 0
            ? ProviderResult<Unit>.Success(default)
            : ProviderResult<Unit>.Failure("not_found", $"the sandbox image {image} is not present: build it first");
    }

    /// <summary>The container's name for a step's key: stable, and safe for Docker.</summary>
    public static string NameOf(string key) => "ff-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20];

    public async Task<ProviderResult<SandboxHandle>> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken)
    {
        var name = NameOf(spec.Key);
        if ((await AttachAsync(spec.Key, cancellationToken)) is { Ok: true } existing)
            return existing;
        var arguments = new List<string>
        {
            "create", "--name", name, "--label", $"{Label}=1", "--label", $"{Label}.key={spec.Key}",
            "--add-host", "host.docker.internal:host-gateway", "--init", "-e", $"HOME={spec.Home}",
        };
        foreach (var (key, value) in spec.Environment)
            arguments.AddRange(["-e", $"{key}={value}"]);
        arguments.AddRange([image, "sleep", "infinity"]);
        if (await Check(cli.RunAsync(arguments, cancellationToken), "create") is { } created)
            return created.Convert<SandboxHandle>();
        if (await Check(cli.RunAsync(["start", name], cancellationToken), "start") is { } started)
            return started.Convert<SandboxHandle>();
        var handle = new SandboxHandle(name, "docker");
        var folders = string.Join(" ", spec.Folders.Append(spec.Home).Select(Quote));
        var homeFiles = string.Join(" ", new[] { ".profile", ".bashrc", ".bash_profile", ".zshenv", ".zshrc" }.Select(f => Quote($"{spec.Home}/{f}")));
        var setup = $"mkdir -p /ff/cmd /ff/bin {folders} && touch {homeFiles} && chown -R {user} {folders} && chmod 755 /ff /ff/bin";
        if (await Root(handle, setup, cancellationToken) is { } failed)
            return failed.Convert<SandboxHandle>();
        return ProviderResult<SandboxHandle>.Success(handle);
    }

    public async Task<ProviderResult<SandboxHandle>> AttachAsync(string key, CancellationToken cancellationToken)
    {
        var name = NameOf(key);
        var inspect = await cli.RunAsync(["inspect", "--format", "{{.State.Running}}", name], cancellationToken);
        if (!inspect.Ok)
            return new ProviderResult<SandboxHandle>(null, inspect.Error);
        if (inspect.Value!.Exit != 0)
            return ProviderResult<SandboxHandle>.Failure("not_found", $"no sandbox for {key}");
        if (inspect.Value.Text.Trim() != "true" && await Check(cli.RunAsync(["start", name], cancellationToken), "start") is { } failed)
            return failed.Convert<SandboxHandle>();
        return ProviderResult<SandboxHandle>.Success(new SandboxHandle(name, "docker"));
    }

    static string CommandId(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    public async Task<ProviderResult<CommandHandle>> StartCommandAsync(SandboxHandle sandbox, SandboxCommand command, string key, CancellationToken cancellationToken)
    {
        var id = CommandId(key);
        var dir = $"/ff/cmd/{id}";
        var exists = await cli.RunAsync(["exec", sandbox.Id, "test", "-f", $"{dir}/pid"], cancellationToken);
        if (!exists.Ok)
            return new ProviderResult<CommandHandle>(null, exists.Error);
        if (exists.Value!.Exit == 0)
            return ProviderResult<CommandHandle>.Success(new CommandHandle(sandbox, key));

        var script = new StringBuilder("set -e\n");
        foreach (var (name, value) in command.Environment ?? new Dictionary<string, string>())
            script.Append($"export {name}={Quote(value)}\n");
        if (command.WorkingDirectory is { } wd)
            script.Append($"cd {Quote(wd)}\n");
        // The command's records (its output, process id and exit code) are root's: the start script runs as root and
        // only the command itself drops to the agent's user, so the agent can neither forge its transcript or exit code
        // nor end the script that records them (LOG 105).
        script.Append("exec ").Append(command.AsRoot ? "" : $"setpriv --reuid={user} --regid={user} --init-groups -- ")
            .Append(string.Join(" ", command.Arguments.Select(Quote))).Append('\n');
        var start = $"cd {dir}\nsetsid sh {dir}/run.sh > {dir}/out 2>&1 < /dev/null &\nchild=$!\necho $child > {dir}/pid\nset +e\nwait $child\necho $? > {dir}/exit\n";
        foreach (var (file, content) in new[] { ("run.sh", script.ToString()), ("start.sh", start) })
            if (await Check(cli.RunAsync(["exec", "-i", "-u", "root", sandbox.Id, "sh", "-c", $"mkdir -p {dir} && cat > {dir}/{file}"], cancellationToken,
                    new MemoryStream(Encoding.UTF8.GetBytes(content))), "write a command") is { } failed)
                return failed.Convert<CommandHandle>();
        if (await Root(sandbox, $"touch {dir}/out && chown -R root {dir} && chmod 700 {dir}", cancellationToken) is { } notOwned)
            return notOwned.Convert<CommandHandle>();
        if (await Check(cli.RunAsync(["exec", "-d", "-u", "root", sandbox.Id, "sh", $"{dir}/start.sh"], cancellationToken), "start a command") is { } notStarted)
            return notStarted.Convert<CommandHandle>();
        // The process id is written by the detached start script; wait for it so a status read never sees a half-started command.
        for (var i = 0; i < 50; i++)
        {
            var ready = await cli.RunAsync(["exec", sandbox.Id, "test", "-s", $"{dir}/pid"], cancellationToken);
            if (ready.Ok && ready.Value!.Exit == 0)
                break;
            await Task.Delay(100, cancellationToken);
        }

        return ProviderResult<CommandHandle>.Success(new CommandHandle(sandbox, key));
    }

    public async Task<ProviderResult<CommandHandle>> AttachCommandAsync(SandboxHandle sandbox, string key, CancellationToken cancellationToken)
    {
        var exists = await cli.RunAsync(["exec", sandbox.Id, "test", "-f", $"/ff/cmd/{CommandId(key)}/pid"], cancellationToken);
        if (!exists.Ok)
            return new ProviderResult<CommandHandle>(null, exists.Error);
        return exists.Value!.Exit == 0
            ? ProviderResult<CommandHandle>.Success(new CommandHandle(sandbox, key))
            : ProviderResult<CommandHandle>.Failure("not_found", $"no command {key}");
    }

    public async Task<ProviderResult<CommandStatus>> GetCommandStatusAsync(CommandHandle command, long cursor, CancellationToken cancellationToken)
    {
        var dir = $"/ff/cmd/{CommandId(command.Key)}";
        var script = $"cd {dir} || exit 3; if [ -f exit ]; then echo \"E:$(cat exit)\"; elif kill -0 \"$(cat pid)\" 2>/dev/null; then echo R; else echo L; fi; tail -c +{cursor + 1} out";
        var run = await cli.RunAsync(["exec", "-u", "root", command.Sandbox.Id, "sh", "-c", script], cancellationToken);
        if (!run.Ok)
            return new ProviderResult<CommandStatus>(null, run.Error);
        if (run.Value!.Exit != 0)
            return ProviderResult<CommandStatus>.Failure("not_found", $"the command {command.Key} cannot be read: {run.Value.Error.Trim()}");
        var bytes = run.Value.Output;
        var newline = Array.IndexOf(bytes, (byte)'\n');
        var head = Encoding.ASCII.GetString(bytes, 0, newline < 0 ? bytes.Length : newline);
        var body = newline < 0 ? [] : bytes[(newline + 1)..];
        var output = Encoding.UTF8.GetString(body);
        var next = cursor + body.Length;
        return head switch
        {
            "R" => ProviderResult<CommandStatus>.Success(new CommandStatus(true, null, output, next)),
            "L" => ProviderResult<CommandStatus>.Success(new CommandStatus(false, null, output, next, Lost: true)),
            _ when head.StartsWith("E:", StringComparison.Ordinal) =>
                ProviderResult<CommandStatus>.Success(new CommandStatus(false, int.Parse(head[2..].Trim(), System.Globalization.CultureInfo.InvariantCulture), output, next)),
            _ => ProviderResult<CommandStatus>.Failure("unexpected_response", $"the command's state reads '{head}'"),
        };
    }

    public async Task<ProviderResult<Unit>> CancelCommandAsync(CommandHandle command, CancellationToken cancellationToken)
    {
        var dir = $"/ff/cmd/{CommandId(command.Key)}";
        var run = await cli.RunAsync(["exec", "-u", "root", command.Sandbox.Id, "sh", "-c", $"[ -f {dir}/exit ] || kill -TERM -- -\"$(cat {dir}/pid)\" 2>/dev/null; true"], cancellationToken);
        return run.Ok ? ProviderResult<Unit>.Success(default) : new ProviderResult<Unit>(default, run.Error);
    }

    public async Task<ProviderResult<Unit>> CopyInAsync(SandboxHandle sandbox, ArtifactRef source, string path, bool readOnly, CancellationToken cancellationToken)
    {
        var got = await store().GetAsync(source, cancellationToken);
        if (!got.Ok)
            return new ProviderResult<Unit>(default, got.Error);
        // A tar stream on standard input: no file on the worker, and the owner and mode are set in the archive.
        var tar = new MemoryStream();
        await using (got.Value)
        await using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            var content = new MemoryStream();
            await got.Value!.CopyToAsync(content, cancellationToken);
            content.Position = 0;
            var entry = new PaxTarEntry(TarEntryType.RegularFile, Path.GetFileName(path))
            {
                DataStream = content,
                Mode = readOnly ? UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
            };
            await writer.WriteEntryAsync(entry, cancellationToken);
        }

        tar.Position = 0;
        var dir = Path.GetDirectoryName(path)!.Replace('\\', '/');
        var owner = readOnly ? "root" : user;
        var script = $"mkdir -p {Quote(dir)} && tar -x -o -C {Quote(dir)} && chown {owner} {Quote(path)}";
        return await Check(cli.RunAsync(["exec", "-i", "-u", "root", sandbox.Id, "sh", "-c", script], cancellationToken, tar), "copy in") is { } failed
            ? failed.Convert<Unit>()
            : ProviderResult<Unit>.Success(default);
    }

    public async Task<ProviderResult<ArtifactRef>> CopyOutAsync(SandboxHandle sandbox, string path, CancellationToken cancellationToken)
    {
        var run = await cli.RunAsync(["exec", "-u", "root", sandbox.Id, "cat", path], cancellationToken);
        if (!run.Ok)
            return new ProviderResult<ArtifactRef>(null, run.Error);
        if (run.Value!.Exit != 0)
            return ProviderResult<ArtifactRef>.Failure("not_found", $"{path} does not exist in the sandbox");
        return await store().PutAsync(new MemoryStream(run.Value.Output), cancellationToken);
    }

    public async Task<ProviderResult<Unit>> MakeReadOnlyAsync(SandboxHandle sandbox, string path, CancellationToken cancellationToken) =>
        await Root(sandbox, $"chown -R root {Quote(path)} && chmod -R a-w {Quote(path)}", cancellationToken) is { } failed
            ? failed.Convert<Unit>()
            : ProviderResult<Unit>.Success(default);

    public async Task<ProviderResult<Unit>> DisposeAsync(SandboxHandle sandbox, CancellationToken cancellationToken)
    {
        var run = await cli.RunAsync(["rm", "-f", sandbox.Id], cancellationToken);
        return run.Ok ? ProviderResult<Unit>.Success(default) : new ProviderResult<Unit>(default, run.Error);
    }

    async Task<ProviderResult<Unit>?> Root(SandboxHandle sandbox, string script, CancellationToken cancellationToken) =>
        await Check(cli.RunAsync(["exec", "-u", "root", sandbox.Id, "sh", "-c", script], cancellationToken), "prepare the sandbox");

    /// <summary>Null when the command ran and exited 0; otherwise the classified failure.</summary>
    static async Task<ProviderResult<Unit>?> Check(Task<ProviderResult<DockerCli.Run>> running, string what)
    {
        var run = await running;
        if (!run.Ok)
            return new ProviderResult<Unit>(default, run.Error);
        return run.Value!.Exit == 0 ? null : ProviderResult<Unit>.Failure("server_error", $"docker could not {what}: {run.Value.Error.Trim()}");
    }

    /// <summary>Quotes a word for <c>sh</c>.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}

static class ResultExtensions
{
    public static ProviderResult<T> Convert<T>(this ProviderResult<Unit> failed) => new(default, failed.Error);
}
