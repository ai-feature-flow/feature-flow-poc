using System.Diagnostics;

namespace FeatureFlow.Integration.Tests.Cluster;

/// <summary>
/// The k3d deployment (deploy/k3d), brought up by <c>deploy/k3d/up.sh deploy/k3d/smoke</c> before these run: its
/// network policies (M4 follow-up 12) and the hosts' clean stop when Kubernetes stops a pod.
/// </summary>
[Trait("Category", "Cluster")]
public class DeploymentTests
{
    const string Engine = "feature-flow", Sandboxes = "feature-flow-sandboxes";

    static readonly Dictionary<string, string> Targets = new()
    {
        ["temporal"] = "temporal.feature-flow.svc:7233",
        ["postgres"] = "postgres.feature-flow.svc:5432",
        ["minio"] = "minio.feature-flow.svc:9000",
        ["api"] = "api.feature-flow.svc:8480",
        ["agent-worker"] = "agent-worker.feature-flow.svc:8481",
        ["internet"] = "https://github.com",
    };

    [Fact]
    public async Task A_sandbox_reaches_only_the_agent_worker_and_the_internet()
    {
        string[] open = ["agent-worker", "internet"];
        Assert.Equal(open, await ProbeAsync(Sandboxes, null, open));
    }

    [Fact]
    public async Task Only_the_hosts_reach_temporal_and_the_stores()
    {
        // The same probe as a host shows the method sees what is open: it reaches everything.
        var all = Targets.Keys.Order().ToArray();
        Assert.Equal(all, await ProbeAsync(Engine, "feature-flow/host=true", all));
        string[] open = ["api", "internet"];
        Assert.Equal(open, await ProbeAsync(Engine, null, open));
    }

    [Theory]
    [InlineData("api")]
    [InlineData("orchestration-worker")]
    [InlineData("agent-worker")]
    public async Task A_host_pod_stops_cleanly_when_Kubernetes_stops_it(string host)
    {
        await Kubectl.OkAsync("-n", Engine, "rollout", "status", $"deployment/{host}", "--timeout=180s");
        var pod = (await Kubectl.OkAsync("-n", Engine, "get", "pod", "-l", $"app.kubernetes.io/name={host}", "--field-selector=status.phase=Running",
            "-o", "jsonpath={.items[0].metadata.name}")).Trim();
        var (logs, output) = Kubectl.Start("-n", Engine, "logs", "-f", pod);
        try
        {
            await WaitForAsync(() => Text(output).Contains("Started"), TimeSpan.FromSeconds(60), $"{pod} never logged Started:\n{Text(output)}");

            // Kubernetes sends SIGTERM and waits 30 seconds (the default grace period) before SIGKILL.
            var stopping = Stopwatch.StartNew();
            await Kubectl.OkAsync("-n", Engine, "delete", "pod", pod, "--wait=true", "--timeout=90s");
            stopping.Stop();
            await logs.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
            Assert.True(Text(output).Contains("Stopped"), $"{pod} did not log Stopped:\n{Text(output)}");
            Assert.True(stopping.Elapsed < TimeSpan.FromSeconds(20), $"{pod} took {stopping.Elapsed} to stop, so it was killed");
        }
        finally
        {
            if (!logs.HasExited)
                logs.Kill();
            logs.Dispose();
        }

        await Kubectl.OkAsync("-n", Engine, "rollout", "status", $"deployment/{host}", "--timeout=180s");
    }

    /// <summary>
    /// Runs a pod in <paramref name="ns"/> that tries each target and returns those it reached, in order. It first waits
    /// up to 30 seconds for each target in <paramref name="open"/>, as the cluster applies policies to a new pod a moment
    /// after it starts; the others are tried once that is done.
    /// </summary>
    static async Task<string[]> ProbeAsync(string ns, string? labels, string[] open)
    {
        var name = $"probe-{Guid.NewGuid():N}"[..20];
        static string Url(string target) => target.StartsWith("https://", StringComparison.Ordinal) ? target : $"http://{target}/";
        // curl's exit 7 is a refused connection and 28 a timeout; any other code means the connection was made.
        var script = $$"""
            try() { curl -s -o /dev/null --connect-timeout 3 -m 6 "$1"; c=$?; [ $c -ne 7 ] && [ $c -ne 28 ]; }
            {{string.Join("\n", open.Select(o => $"for i in $(seq 30); do try {Url(Targets[o])} && break; sleep 1; done"))}}
            {{string.Join("\n", Targets.Select(t => $"try {Url(t.Value)} && echo reached {t.Key}"))}}
            echo done
            """;
        string[] arguments = ["-n", ns, "run", name, "--image=curlimages/curl:8.17.0", "--restart=Never", "--command"];
        if (labels is not null)
            arguments = [.. arguments, $"--labels={labels}"];
        await Kubectl.OkAsync([.. arguments, "--", "sh", "-c", script]);
        try
        {
            await Kubectl.OkAsync("-n", ns, "wait", $"pod/{name}", "--for=jsonpath={.status.phase}=Succeeded", "--timeout=180s");
            var log = await Kubectl.OkAsync("-n", ns, "logs", name);
            Assert.Contains("done", log);
            return log.Split('\n').Where(l => l.StartsWith("reached ", StringComparison.Ordinal)).Select(l => l["reached ".Length..].Trim()).Order().ToArray();
        }
        finally
        {
            await Kubectl.RunAsync("-n", ns, "delete", "pod", name, "--wait=false");
        }
    }

    static string Text(System.Text.StringBuilder output)
    {
        lock (output)
            return output.ToString();
    }

    static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, message);
            await Task.Delay(200);
        }
    }
}
