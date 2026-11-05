using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;

namespace FeatureFlow.Providers.Abstractions;

/// <summary>What a shell script run as root in a sandbox printed, and how it exited.</summary>
public sealed record ShellRun(int Exit, byte[] Output, string Error)
{
    public string Text => Encoding.UTF8.GetString(Output);
}

/// <summary>
/// The part of a sandbox provider that is the same for every Linux container platform (docs/06 section 3.1): commands,
/// copies and the agent's user, over one primitive, <see cref="ExecAsync"/>, which runs a shell script as root in the
/// sandbox. The Docker and Kubernetes sandboxes differ only in how a sandbox is created, found, reached and removed.
/// <para>
/// Commands are started detached in their own process group, with their output, process id and exit code in files
/// under <c>/ff/cmd/&lt;id&gt;</c>, so they are polled rather than awaited and survive the worker (risk R18). The
/// files are root's and the command alone drops to the agent's user, so the agent can neither forge its transcript
/// or exit code nor end the script that records them (LOG 105).
/// </para>
/// </summary>
public abstract class ShellSandbox(string user, Func<IArtifactStoreProvider> store) : ISandboxProvider
{
    protected string User { get; } = user;

    public abstract ProviderDescription Describe();

    public abstract Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken);

    public abstract Task<ProviderResult<SandboxHandle>> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken);

    public abstract Task<ProviderResult<SandboxHandle>> AttachAsync(string key, CancellationToken cancellationToken);

    public abstract Task<ProviderResult<Unit>> DisposeAsync(SandboxHandle sandbox, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>sh -c <paramref name="script"/></c> as root in the sandbox, with <paramref name="input"/> on standard input. A
    /// script reads its input with <c>head -c</c>, never to end of file, so a platform that cannot close standard input works.
    /// </summary>
    protected abstract Task<ProviderResult<ShellRun>> ExecAsync(SandboxHandle sandbox, string script, Stream? input, CancellationToken cancellationToken);

    /// <summary>A sandbox's name for a step's key: stable, so a new worker finds it again, and a valid name on every platform.</summary>
    public static string NameOf(string key) => "ff-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20];

    /// <summary>Creates the sandbox's folders and an empty home with empty shell startup files (DR19), owned by the agent's user.</summary>
    protected Task<ProviderResult<Unit>?> PrepareAsync(SandboxHandle handle, SandboxSpec spec, CancellationToken cancellationToken)
    {
        var folders = string.Join(" ", spec.Folders.Append(spec.Home).Select(Quote));
        var homeFiles = string.Join(" ", new[] { ".profile", ".bashrc", ".bash_profile", ".zshenv", ".zshrc" }.Select(f => Quote($"{spec.Home}/{f}")));
        return Root(handle, $"mkdir -p /ff/cmd /ff/bin {folders} && touch {homeFiles} && chown -R {User} {folders} && chmod 755 /ff /ff/bin", cancellationToken);
    }

    static string CommandId(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    public async Task<ProviderResult<CommandHandle>> StartCommandAsync(SandboxHandle sandbox, SandboxCommand command, string key, CancellationToken cancellationToken)
    {
        var id = CommandId(key);
        var dir = $"/ff/cmd/{id}";
        var exists = await ExecAsync(sandbox, $"test -f {dir}/pid", null, cancellationToken);
        if (!exists.Ok)
            return new ProviderResult<CommandHandle>(null, exists.Error);
        if (exists.Value!.Exit == 0)
            return ProviderResult<CommandHandle>.Success(new CommandHandle(sandbox, key));

        var script = new StringBuilder("set -e\n");
        foreach (var (name, value) in command.Environment ?? new Dictionary<string, string>())
            script.Append($"export {name}={Quote(value)}\n");
        if (command.WorkingDirectory is { } wd)
            script.Append($"cd {Quote(wd)}\n");
        script.Append("exec ").Append(command.AsRoot ? "" : $"setpriv --reuid={User} --regid={User} --init-groups --no-new-privs -- ")
            .Append(string.Join(" ", command.Arguments.Select(Quote))).Append('\n');
        var start = $"cd {dir}\nsetsid sh {dir}/run.sh > {dir}/out 2>&1 < /dev/null &\nchild=$!\necho $child > {dir}/pid\nset +e\nwait $child\necho $? > {dir}/exit\n";
        foreach (var (file, content) in new[] { ("run.sh", script.ToString()), ("start.sh", start) })
            if (await Write(sandbox, $"{dir}/{file}", Encoding.UTF8.GetBytes(content), cancellationToken) is { } failed)
                return failed.Convert<CommandHandle>();
        // The start script is detached from this exec (its own session, no inherited streams), so it outlives it.
        if (await Root(sandbox, $"touch {dir}/out && chown -R root {dir} && chmod 700 {dir} && (setsid sh {dir}/start.sh > /dev/null 2>&1 < /dev/null &)",
                cancellationToken) is { } notStarted)
            return notStarted.Convert<CommandHandle>();
        // The process id is written by the detached start script; wait for it so a status read never sees a half-started command.
        for (var i = 0; i < 50; i++)
        {
            var ready = await ExecAsync(sandbox, $"test -s {dir}/pid", null, cancellationToken);
            if (ready.Ok && ready.Value!.Exit == 0)
                break;
            await Task.Delay(100, cancellationToken);
        }

        return ProviderResult<CommandHandle>.Success(new CommandHandle(sandbox, key));
    }

    public async Task<ProviderResult<CommandHandle>> AttachCommandAsync(SandboxHandle sandbox, string key, CancellationToken cancellationToken)
    {
        var exists = await ExecAsync(sandbox, $"test -f /ff/cmd/{CommandId(key)}/pid", null, cancellationToken);
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
        var run = await ExecAsync(command.Sandbox, script, null, cancellationToken);
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
        // dash's kill takes no "--": a negative process id names the command's process group.
        var run = await ExecAsync(command.Sandbox, $"[ -f {dir}/exit ] || kill -TERM -\"$(cat {dir}/pid)\" 2>/dev/null; true", null, cancellationToken);
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
        var owner = readOnly ? "root" : User;
        var script = $"mkdir -p {Quote(dir)} && head -c {tar.Length} | tar -x -o -C {Quote(dir)} && chown {owner} {Quote(path)}";
        return await Check(ExecAsync(sandbox, script, tar, cancellationToken), "copy in") is { } failed
            ? failed.Convert<Unit>()
            : ProviderResult<Unit>.Success(default);
    }

    public async Task<ProviderResult<ArtifactRef>> CopyOutAsync(SandboxHandle sandbox, string path, CancellationToken cancellationToken)
    {
        var run = await ExecAsync(sandbox, $"cat {Quote(path)}", null, cancellationToken);
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

    /// <summary>Writes a root-owned file in the sandbox.</summary>
    protected Task<ProviderResult<Unit>?> Write(SandboxHandle sandbox, string path, byte[] content, CancellationToken cancellationToken) =>
        Check(ExecAsync(sandbox, $"mkdir -p {Quote(Path.GetDirectoryName(path)!.Replace('\\', '/'))} && head -c {content.Length} > {Quote(path)}",
            new MemoryStream(content), cancellationToken), "write a file");

    /// <summary>Runs a script as root; null when it exited 0, otherwise the classified failure.</summary>
    protected Task<ProviderResult<Unit>?> Root(SandboxHandle sandbox, string script, CancellationToken cancellationToken) =>
        Check(ExecAsync(sandbox, script, null, cancellationToken), "prepare the sandbox");

    /// <summary>Null when the script ran and exited 0; otherwise the classified failure.</summary>
    protected static async Task<ProviderResult<Unit>?> Check(Task<ProviderResult<ShellRun>> running, string what)
    {
        var run = await running;
        if (!run.Ok)
            return new ProviderResult<Unit>(default, run.Error);
        return run.Value!.Exit == 0 ? null : ProviderResult<Unit>.Failure("server_error", $"the sandbox could not {what}: {run.Value.Error.Trim()}");
    }

    /// <summary>
    /// The script that closes a sandbox's network to everything but <paramref name="reach"/> and the public internet (M4
    /// follow-up 12): loopback, DNS to the sandbox's resolvers, TCP to each reachable host and port, then every private,
    /// shared, link-local and loopback range refused; anything else is the internet. Run as root at creation; the agent's
    /// user has no capability to change it. For a platform that filters inside the sandbox (Docker); Kubernetes uses a
    /// network policy.
    /// </summary>
    public static string NetworkScript(IEnumerable<Uri> reach)
    {
        var s = new StringBuilder("set -e\n");
        s.Append("for t in iptables ip6tables; do $t -F OUTPUT; $t -A OUTPUT -o lo -j ACCEPT; $t -A OUTPUT -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT; done\n");
        s.Append("allow() { case $1 in *:*) t=ip6tables;; *) t=iptables;; esac; $t -A OUTPUT -d \"$1\" -p \"$2\" --dport \"$3\" -j ACCEPT; }\n");
        s.Append("for ns in $(awk '/^nameserver/{print $2}' /etc/resolv.conf); do allow \"$ns\" udp 53; allow \"$ns\" tcp 53; done\n");
        foreach (var (host, port) in reach.Select(u => (u.IdnHost, u.Port)).Distinct())
            s.Append($"ips=$(getent ahosts {Quote(host)} | awk '{{print $1}}' | sort -u); [ -n \"$ips\" ] || {{ echo 'cannot resolve {host}' >&2; exit 1; }}\n")
                .Append($"for ip in $ips; do allow \"$ip\" tcp {port}; done\n");
        foreach (var net in new[] { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/3" })
            s.Append($"iptables -A OUTPUT -d {net} -j REJECT\n");
        foreach (var net in new[] { "::1/128", "fc00::/7", "fe80::/10", "ff00::/8", "::ffff:0:0/96" })
            s.Append($"ip6tables -A OUTPUT -d {net} -j REJECT\n");
        return s.ToString();
    }

    /// <summary>
    /// <see cref="NetworkScript"/>, run only when the last rule of each family is missing: for every attach, so a container
    /// restarted without its rules (by a crash, a failed or cancelled attach) is closed again, while a running agent's network
    /// is never flushed and reopened (review R2, F1).
    /// </summary>
    public static string EnsureNetworkScript(IEnumerable<Uri> reach) =>
        "if iptables -C OUTPUT -d 224.0.0.0/3 -j REJECT 2>/dev/null && ip6tables -C OUTPUT -d ::ffff:0:0/96 -j REJECT 2>/dev/null; then exit 0; fi\n"
        + NetworkScript(reach);

    /// <summary>Quotes a word for <c>sh</c>.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}

public static class ProviderResultExtensions
{
    /// <summary>A failed result of one type as the same failure of another.</summary>
    public static ProviderResult<T> Convert<T>(this ProviderResult<Unit> failed) => new(default, failed.Error);
}
