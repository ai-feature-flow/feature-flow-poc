using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FeatureFlow.Cli;

namespace FeatureFlow.Integration.Tests.Cluster;

/// <summary>
/// A run on the k3d deployment with the smoke configuration (deploy/k3d/smoke): started through the API's pod, driven by
/// the orchestration worker's pod and the agent worker's pod, recorded in Postgres and MinIO, all in the cluster. Its
/// agent step runs in a Kubernetes sandbox pod, on a checkout of trial/app from the trial Git server, where the scripted
/// agent changes it through the sandbox provider; the engine commits the change and pushes it to the work branch.
/// </summary>
[Trait("Category", "Cluster")]
[Collection("Cluster")]
public class ClusterRunTests
{
    [Fact]
    public async Task A_run_completes_across_the_three_hosts_with_its_agent_step_in_a_sandbox_pod()
    {
        var (forward, output) = Kubectl.Start("-n", "feature-flow", "port-forward", "svc/api", ":8480");
        var token = Path.Combine(Path.GetTempPath(), $"ff-cluster-token-{Guid.NewGuid():N}");
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            Match port;
            while (!(port = Regex.Match(Text(output), @"Forwarding from 127\.0\.0\.1:(\d+)")).Success)
            {
                Assert.True(DateTime.UtcNow < deadline && !forward.HasExited, $"kubectl port-forward did not start: {Text(output)}");
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }

            var api = $"http://127.0.0.1:{port.Groups[1].Value}";
            var secret = await Kubectl.OkAsync("-n", "feature-flow", "get", "secret", "feature-flow-infrastructure", "-o", "jsonpath={.data.api-token}");
            await File.WriteAllBytesAsync(token, Convert.FromBase64String(secret.Trim()), TestContext.Current.CancellationToken);

            var ticket = $"FF-{Guid.NewGuid():N}"[..20];
            var (started, start, startError) = Cli(["start", "smoke:templates/change", "--repo", "trial/app", "--param", $"ticket_id={ticket}"], api, token);
            Assert.True(started == 0, start + startError);
            var id = start.Trim().Split(' ')[1];
            var (ended, end, endError) = Cli(["wait", id, "--timeout", "240"], api, token);
            Assert.True(ended == 0 && end.Contains("completed"), end + endError + Cli(["show", id], api, token).Output);

            // The agent step ran on the agent worker's queue, which only the agent worker's pod serves.
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", await File.ReadAllTextAsync(token, TestContext.Current.CancellationToken));
            var view = JsonNode.Parse(await http.GetStringAsync($"{api}/runs/{id}", TestContext.Current.CancellationToken))!;
            Assert.Contains("implement", Harness.StartedSteps(view));

            // The scripted agent's edit, made in the sandbox pod, is on the work branch in the Git server: checked out, committed
            // and pushed on the real path.
            var edit = await Kubectl.OkAsync("-n", "feature-flow", "exec", "deploy/git", "--", "git", "--git-dir", "/srv/git/trial/app.git", "show",
                $"refs/heads/ff/smoke/{ticket}:work/{id}/implement.txt");
            Assert.StartsWith("implement execution 1", edit);
        }
        finally
        {
            if (!forward.HasExited)
                forward.Kill();
            forward.Dispose();
            File.Delete(token);
        }
    }

    static (int Exit, string Output, string Error) Cli(string[] args, string api, string token)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = CliApp.Run(["run", .. args, "--api", api, "--token-file", token], output, error);
        return (exit, output.ToString(), error.ToString());
    }

    static string Text(System.Text.StringBuilder output)
    {
        lock (output)
            return output.ToString();
    }
}
