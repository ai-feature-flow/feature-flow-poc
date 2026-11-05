using System.Text;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The sandbox provider's contract (docs/06 section 3.1), lifted from the Docker sandbox's behaviour: a sandbox is found
/// again by its key and gone once disposed; commands run as the agent's unprivileged user, are polled, found again by key
/// and cancelled, and their records cannot be forged by the agent; copies round-trip; a read-only path cannot be changed
/// by the agent; the home is the engine's (DR19).
/// <para>
/// The network checks (M4 follow-up 12): a sandbox reaches the engine's endpoint (<see cref="Reachable"/>, standing for
/// the agent worker's <c>urls</c> and model proxy) and the public internet, and never <see cref="Forbidden"/>: the
/// worker's host's other ports, the stores, Temporal and other services of its host or cluster. The agent cannot lift
/// the restriction. A sandbox provider's test project subclasses it; each test creates and disposes of its own sandbox.
/// </para>
/// </summary>
public abstract class SandboxContract : ContractBase, IAsyncLifetime
{
    protected abstract ISandboxProvider Sandboxes { get; }

    /// <summary>An artifact store the provider was created with, for copies.</summary>
    protected abstract IArtifactStoreProvider Store { get; }

    /// <summary>An endpoint the provider was told the sandbox must reach: it stands for the agent worker.</summary>
    protected abstract Uri Reachable { get; }

    /// <summary>Endpoints on the worker's host or network that the sandbox must not reach, each listening.</summary>
    protected abstract IReadOnlyList<Uri> Forbidden { get; }

    /// <summary>An endpoint on the public internet: a code host or package registry.</summary>
    protected virtual Uri Public => new("https://github.com");

    protected readonly string Key = "contract/" + Guid.NewGuid().ToString("N");
    protected const string Home = "/ff/home";
    protected SandboxHandle Sandbox { get; private set; } = null!;

    public virtual async ValueTask InitializeAsync() =>
        Sandbox = Ok(await Sandboxes.CreateAsync(new SandboxSpec(Key, Home, new Dictionary<string, string> { ["FF_CONTRACT"] = "yes" }, ["/ff/work", "/ff/out"]), Ct));

    public virtual async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await Sandboxes.DisposeAsync(Sandbox, CancellationToken.None);
    }

    int commands;

    /// <summary>Runs a command to its end, as the agent's user unless <paramref name="root"/>.</summary>
    protected async Task<(int Exit, string Output)> RunAsync(string script, bool root = false) =>
        Ok(await Sandboxes.RunAsync(Sandbox, new SandboxCommand(["sh", "-c", script], AsRoot: root), $"{Key}/cmd-{++commands}", Ct, TimeSpan.FromMinutes(2)));

    /// <summary>Whether the agent's user opens a TCP connection to the endpoint within a few seconds.</summary>
    protected async Task<bool> ConnectsAsync(Uri endpoint)
    {
        var (exit, _) = await RunAsync($"timeout 8 bash -c '</dev/tcp/{endpoint.IdnHost.Trim('[', ']')}/{endpoint.Port}'");
        return exit == 0;
    }

    [Fact]
    public async Task A_sandbox_is_found_again_by_its_key_and_is_gone_once_disposed()
    {
        var again = Ok(await Sandboxes.CreateAsync(new SandboxSpec(Key, Home, new Dictionary<string, string>(), []), Ct));
        var attached = Ok(await Sandboxes.AttachAsync(Key, Ct));

        Assert.Equal(Sandbox, again);
        Assert.Equal(Sandbox, attached);
        Ok(await Sandboxes.DisposeAsync(Sandbox, Ct));
        Refused(await Sandboxes.AttachAsync(Key, Ct), "not_found");
        Refused(await Sandboxes.AttachAsync("contract/never-created-" + Guid.NewGuid().ToString("N"), Ct), "not_found");
    }

    [Fact]
    public async Task A_command_runs_as_the_agents_user_with_its_output_and_exit_code()
    {
        var (exit, output) = await RunAsync("id -u; printf 'line one\\nline two\\n'; exit 3");
        var (rootExit, rootOutput) = await RunAsync("id -u", root: true);

        Assert.Equal(3, exit);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEqual("0", lines[0]);
        Assert.Equal(["line one", "line two"], lines[1..]);
        Assert.Equal((0, "0"), (rootExit, rootOutput.Trim()));
    }

    [Fact]
    public async Task A_running_command_is_found_again_by_its_key_and_can_be_cancelled()
    {
        var key = $"{Key}/long";
        var handle = Ok(await Sandboxes.StartCommandAsync(Sandbox, new SandboxCommand(["sh", "-c", "echo started; sleep 120; echo ended"]), key, Ct));
        var status = Ok(await Sandboxes.GetCommandStatusAsync(handle, 0, Ct));
        var again = Ok(await Sandboxes.StartCommandAsync(Sandbox, new SandboxCommand(["sh", "-c", "echo second start"]), key, Ct));
        var attached = Ok(await Sandboxes.AttachCommandAsync(Sandbox, key, Ct));

        Assert.True(status.Running);
        Assert.Equal(handle, again);
        Assert.Equal(handle, attached);
        Refused(await Sandboxes.AttachCommandAsync(Sandbox, $"{Key}/never-started", Ct), "not_found");

        Ok(await Sandboxes.CancelCommandAsync(handle, Ct));
        CommandStatus ended;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        do
        {
            await Task.Delay(200, Ct);
            ended = Ok(await Sandboxes.GetCommandStatusAsync(handle, 0, Ct));
        }
        while (ended.Running && DateTimeOffset.UtcNow < deadline);

        Assert.False(ended.Running);
        Assert.Equal("started\n", ended.Output);
    }

    [Fact]
    public async Task A_command_that_ends_reads_as_ended_and_one_killed_with_its_sandboxs_processes_as_lost()
    {
        var marker = "lost-" + Guid.NewGuid().ToString("N");
        var handle = Ok(await Sandboxes.StartCommandAsync(Sandbox, new SandboxCommand(["sh", "-c", $"echo {marker}; sleep 117"]), $"{Key}/lost", Ct));
        Assert.True(Ok(await Sandboxes.GetCommandStatusAsync(handle, 0, Ct)).Running);

        // As a sandbox restart would: the command and the process waiting on it end, and nothing records an exit code.
        await RunAsync($"p=$(pgrep -f '{marker}' | head -1); kill -9 \"$(ps -o ppid= -p \"$p\")\" \"$p\"", root: true);
        CommandStatus status;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        do
        {
            await Task.Delay(200, Ct);
            status = Ok(await Sandboxes.GetCommandStatusAsync(handle, 0, Ct));
        }
        while (status.Running && DateTimeOffset.UtcNow < deadline);

        Assert.Equal((false, true, null), (status.Running, status.Lost, status.ExitCode));
        // Many short commands in a row each read as ended with their own code, never as lost (an ended command's code is
        // written a moment after it ends).
        for (var i = 0; i < 20; i++)
            Assert.Equal(i % 3, (await RunAsync($"exit {i % 3}")).Exit);
    }

    [Fact]
    public async Task Output_is_read_from_the_cursor()
    {
        var handle = Ok(await Sandboxes.StartCommandAsync(Sandbox, new SandboxCommand(["sh", "-c", "printf abc; sleep 1; printf def"]), $"{Key}/cursor", Ct));
        var all = "";
        long cursor = 0;
        CommandStatus status;
        do
        {
            await Task.Delay(200, Ct);
            status = Ok(await Sandboxes.GetCommandStatusAsync(handle, cursor, Ct));
            all += status.Output;
            cursor = status.Cursor;
        }
        while (status.Running);

        Assert.Equal("abcdef", all);
        Assert.Equal((0, 6L), (status.ExitCode, cursor));
        Assert.Equal("cdef", Ok(await Sandboxes.GetCommandStatusAsync(handle, 2, Ct)).Output);
    }

    /// <summary>Review R2, F4: the agent's processes run under a seccomp filter and cannot gain privileges (no setuid escalation).</summary>
    [Fact]
    public async Task The_agent_cannot_gain_privileges_and_runs_under_seccomp()
    {
        var (exit, output) = await RunAsync("grep -E '^(NoNewPrivs|Seccomp):' /proc/self/status");

        Assert.Equal(0, exit);
        Assert.Matches(@"NoNewPrivs:\s+1", output);
        Assert.Matches(@"Seccomp:\s+2", output);
    }

    [Fact]
    public async Task The_agent_cannot_forge_its_commands_records()
    {
        var (exit, _) = await RunAsync("for d in /ff/cmd/*; do echo 0 > $d/exit && exit 0; done; exit 1");

        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task Copies_round_trip_and_a_read_only_copy_cannot_be_changed_by_the_agent()
    {
        var bytes = Encoding.UTF8.GetBytes("contract " + Key + "\n" + new string('x', 300_000));
        var reference = Ok(await Store.PutAsync(new MemoryStream(bytes), Ct));

        Ok(await Sandboxes.CopyInAsync(Sandbox, reference, "/ff/work/sub/data.txt", readOnly: false, Ct));
        Ok(await Sandboxes.CopyInAsync(Sandbox, reference, "/ff/bin/tool", readOnly: true, Ct));
        var back = Ok(await Sandboxes.CopyOutAsync(Sandbox, "/ff/work/sub/data.txt", Ct));
        var (writable, _) = await RunAsync("echo more >> /ff/work/sub/data.txt");
        var (readOnly, _) = await RunAsync("echo more >> /ff/bin/tool || rm -f /ff/bin/tool");

        Assert.Equal(reference, back);
        Assert.Equal(0, writable);
        Assert.NotEqual(0, readOnly);
        Refused(await Sandboxes.CopyOutAsync(Sandbox, "/ff/work/absent.txt", Ct), "not_found");
    }

    [Fact]
    public async Task A_path_made_read_only_cannot_be_changed_by_the_agent()
    {
        Assert.Equal(0, (await RunAsync("mkdir -p /ff/work/repo && echo a > /ff/work/repo/file")).Exit);

        Ok(await Sandboxes.MakeReadOnlyAsync(Sandbox, "/ff/work/repo", Ct));

        Assert.NotEqual(0, (await RunAsync("echo b > /ff/work/repo/file")).Exit);
        Assert.NotEqual(0, (await RunAsync("touch /ff/work/repo/new")).Exit);
        Assert.Equal("a", (await RunAsync("cat /ff/work/repo/file")).Output.Trim());
    }

    [Fact]
    public async Task The_home_is_the_engines_with_empty_startup_files_and_the_given_environment()
    {
        var (exit, output) = await RunAsync($"echo \"$HOME|$FF_CONTRACT\"; for f in .profile .bashrc .bash_profile .zshenv .zshrc; do [ -f {Home}/$f ] && [ ! -s {Home}/$f ] || echo bad $f; done; touch {Home}/x /ff/out/x");

        Assert.Equal((0, $"{Home}|yes"), (exit, output.Trim()));
    }

    [Fact]
    public async Task The_sandbox_reaches_the_engines_endpoint_and_the_internet()
    {
        Assert.True(await ConnectsAsync(Reachable), $"the sandbox does not reach {Reachable}");
        Assert.True(await ConnectsAsync(Public), $"the sandbox does not reach {Public}");
    }

    [Fact]
    public async Task The_sandbox_reaches_nothing_else_on_the_workers_host_or_network()
    {
        foreach (var endpoint in Forbidden)
            Assert.False(await ConnectsAsync(endpoint), $"the sandbox reaches {endpoint}");
    }

    [Fact]
    public async Task The_agent_cannot_lift_the_restriction()
    {
        await RunAsync("iptables -F OUTPUT; ip6tables -F OUTPUT; iptables -P OUTPUT ACCEPT; true");

        foreach (var endpoint in Forbidden)
            Assert.False(await ConnectsAsync(endpoint), $"after the agent's attempt, the sandbox reaches {endpoint}");
    }
}
