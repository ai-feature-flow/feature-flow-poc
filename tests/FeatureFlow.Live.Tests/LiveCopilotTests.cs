using FeatureFlow.Integration.Tests;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Live.Tests;

/// <summary>
/// SP5's checks for Copilot CLI against the real world (docs/spikes/sp5-results.md; docs/11 M8, verified by), on Copilot
/// quota (LOG 496, 498): Copilot CLI in the sandbox in its native GitHub mode, its Copilot API pointed at the proxy, which
/// alone holds the GitHub token (<c>FF_COPILOT_GITHUB_TOKEN</c>) and forwards to the account's Copilot API
/// (<c>FF_COPILOT_API_URL</c>); the fixture repository through the GitHub App. C1 to C4: a real step completes and
/// commits, and the token's value is in none of its environment, home, transcript or commits. N1: with the model
/// unreachable it does not succeed. N2: with a key GitHub refuses it does not succeed, having no other credential to fall
/// back to. Each test removes the references it pushed. Run C1 alone with
/// <c>--filter "FullyQualifiedName~A_copilot_implementer_step"</c>.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveCopilotTests
{
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(8);
    internal static readonly AgentHarnessOptions Live = new() { Live = true, Runtime = "copilot_cli", CommitInterval = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// N1's model address: nothing listens there, so the connection is refused before TLS. https, as <c>auth: github</c>
    /// needs (LOG 496), or composition refuses the host before the run starts.
    /// </summary>
    internal static readonly AgentHarnessOptions Unreachable = Live with { ModelUpstream = "https://127.0.0.1:9" };

    /// <summary>N2's key, a token GitHub refuses.</summary>
    internal static readonly AgentHarnessOptions Refused = Live with { ModelKey = "github_pat_ffLiveN2NotAValidToken" };

    static string Ticket(string name) => $"LIVE-copilot-{name}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

    /// <summary>A line for the results note, appended to the file <c>FF_LIVE_LOG</c> names. Never a credential.</summary>
    static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("FF_LIVE_LOG") is { Length: > 0 } path)
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {line}\n");
    }

    /// <summary>
    /// The run, its transcripts, and the model requests the proxy recorded, the engine's record of usage: each is
    /// "<c>METHOD /path status in=N out=N model=M</c>".
    /// </summary>
    static async Task LogRunAsync(AgentHarness h, string test, string runId)
    {
        var events = await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, AgentScripts.Key(runId));
        var reports = await h.RecordsAsync<ReportRecord>(Queries.Reports, AgentScripts.Key(runId));
        var model = events.Where(e => e.Kind == "model").Select(e => e.Detail.Split(' ')).Where(d => d.Length >= 6).ToList();
        var transcripts = (await h.TimelineAsync(runId)).Split('\n').Where(l => l.Contains("transcript of session", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf("transcript of session", StringComparison.Ordinal) + 14)..]);
        Log($"copilot {test}: run {runId}; reports {string.Join(",", reports.Select(r => r.Accepted ? "accepted" : "rejected"))}; " +
            $"sessions {string.Join(",", events.Where(e => e.Kind == "session").Select(e => e.Detail))}; transcripts {string.Join(", ", transcripts)}; " +
            $"model requests {model.Count} ({string.Join(", ", model.GroupBy(d => $"{d[0]} {d[1]} {d[2]}").Select(g => $"{g.Key} x{g.Count()}"))}); " +
            $"models {string.Join(",", model.Select(d => d[5]["model=".Length..]).Distinct())}; " +
            $"usage in={model.Sum(d => long.Parse(d[3]["in=".Length..]))} out={model.Sum(d => long.Parse(d[4]["out=".Length..]))}; " +
            $"hook calls {events.Count(e => e.Kind.StartsWith("hook_", StringComparison.Ordinal))}");
    }

    async Task CleanUpAsync(AgentHarness h, string test, string runId, string ticket)
    {
        await LogRunAsync(h, test, runId);
        await h.StopRunAsync(runId);
        await h.DeleteRemoteReferencesAsync($"refs/heads/ff/ticket/{ticket}", AgentHarness.HiddenReference(runId, "implement"));
    }

    /// <summary>The names of the places whose text holds <paramref name="secret"/>; never the value itself.</summary>
    internal static List<string> Holding(string secret, IReadOnlyDictionary<string, string> places) =>
        places.Where(p => p.Value.Contains(secret, StringComparison.Ordinal)).Select(p => p.Key).ToList();

    const string Finish = " Then write notes.md in your output folder saying what you did, and report the outcome implemented with outputs answer set to none" +
                          " and the artifact notes set to notes.md.";

    /// <summary>
    /// What the agent records of its sandbox: its shell's environment, every process's it can read (the CLI's own,
    /// with the step token), the size of its shell's startup files (the engine's, empty: DR19), and every file under its
    /// home and Copilot's home. Errors are discarded, so a file it cannot read does not stop the rest.
    /// </summary>
    const string Dump = "{ env | sort; echo '--- processes'; for f in /proc/[0-9]*/environ; do tr '\\0' '\\n' < \"$f\"; done | sort -u; " +
                        "echo '--- startup bytes'; cat \"$HOME/.profile\" \"$HOME/.bashrc\" \"$HOME/.bash_profile\" \"$HOME/.zshenv\" \"$HOME/.zshrc\" | wc -c; " +
                        "echo '--- home'; find \"$HOME\" /ff/copilot -type f -size -4M -exec cat {} +; echo '--- end'; } > /ff/out/notes.md 2>/dev/null";

    /// <summary>
    /// C1 to C4: a real implementer step on Copilot CLI commits, and the commit reaches the work branch; the GitHub token
    /// the proxy holds is in none of the agent's environment, its processes' environments, its homes, the transcript or
    /// the commits.
    /// </summary>
    [Fact]
    public async Task A_copilot_implementer_step_on_the_fixture_repository_commits_and_its_sandbox_holds_no_credential()
    {
        await using var h = await AgentHarness.StartAsync("live-copilot", null, Live);
        var ticket = Ticket("done");
        var runId = h.Start(ticket, $"Create the file ff-live/{ticket}.md holding the single line 'Feature Flow live test {ticket}', and commit it " +
                                    $"with git, with the message 'Add {ticket} note'. Then run this command with your shell tool, exactly: {Dump} . " +
                                    "Then report the outcome implemented with outputs answer set to none and the artifact notes set to notes.md.");
        try
        {
            var status = await h.WaitForEndAsync(runId, RunTimeout);
            var timeline = await h.TimelineAsync(runId);
            Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
            var branch = $"refs/heads/ff/ticket/{ticket}";
            var final = await h.RemoteRevisionAsync(branch);
            Assert.NotEqual(h.MainRevision, final);
            Assert.Contains("session 1 started on runtime copilot_cli", timeline);

            var notes = await h.ArtifactTextAsync(AgentScripts.Reference(timeline, "artifact notes: "));
            var transcript = await h.ArtifactTextAsync(AgentScripts.Reference(timeline, "transcript of session 1: "));
            var commits = await h.RemoteChangesAsync(branch);
            Assert.Contains($"Add {ticket} note", commits);
            // The dump ran to its end, in the CLI's own shell, so the checks below read the agent's real view, not an empty file.
            Assert.Contains("HOME=/ff/home", notes);
            Assert.Contains("COPILOT_AGENT_SESSION_ID=", notes);
            Assert.Contains("--- processes", notes);
            Assert.Contains("--- startup bytes\n0\n", notes);
            Assert.Contains("--- end", notes);
            var lines = notes.Split('\n');
            foreach (var name in new[] { "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY", "COPILOT_PROVIDER_API_KEY", "GH_TOKEN", "GITHUB_TOKEN", AgentHarness.CopilotKey })
                Assert.DoesNotContain(lines, l => l.StartsWith(name + "=", StringComparison.Ordinal));
            var key = Environment.GetEnvironmentVariable(AgentHarness.CopilotKey)!;
            var holding = Holding(key, new Dictionary<string, string> { ["environment and home"] = notes, ["transcript"] = transcript, ["commits"] = commits });
            Assert.True(holding.Count == 0, "the GitHub token is in: " + string.Join(", ", holding));

            var model = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, AgentScripts.Key(runId))).Where(e => e.Kind == "model").ToList();
            // To tighten after the first live run, once the requests the CLI makes are known: every POST 200, and GET meta and models 200.
            Assert.Contains(model, e => e.Detail.StartsWith("POST ", StringComparison.Ordinal) && e.Detail.Contains(" 200 ", StringComparison.Ordinal));
            Log($"copilot C1: run {runId}; branch at {final![..10]}; the key is absent from the environment, processes, homes, transcript and commits; " +
                $"the CLI's own environment was {(notes.Contains("COPILOT_GITHUB_TOKEN=ghs_", StringComparison.Ordinal) ? "" : "not ")}readable");
        }
        finally
        {
            await CleanUpAsync(h, "C1", runId, ticket);
        }
    }

    /// <summary>N1: the model is unreachable behind the proxy; the step stops resumably, uses no attempt and does not succeed.</summary>
    [Fact]
    public async Task With_the_model_unreachable_the_real_copilot_step_does_not_succeed()
    {
        await using var h = await AgentHarness.StartAsync("live-copilot-n1", null, Unreachable);
        var ticket = Ticket("n1");
        var runId = h.Start(ticket, "Change nothing in the repository. Write notes.md in your output folder saying hello." + Finish);
        try
        {
            var deadline = DateTime.UtcNow + RunTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var sessions = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, AgentScripts.Key(runId))).Where(e => e.Kind == "session").Select(e => e.Detail).ToList();
                if (sessions.Count(s => s.EndsWith("ended model_unreachable", StringComparison.Ordinal)) >= 2)
                    break;
                await Task.Delay(2000, TestContext.Current.CancellationToken);
            }

            var view = await h.ShowAsync(runId);
            Assert.Equal("running", view["run"]!["status"]!.ToString());
            Assert.DoesNotContain("attempt_failed", await h.TimelineAsync(runId));
            Assert.Empty(await h.RecordsAsync<ReportRecord>(Queries.Reports, AgentScripts.Key(runId)));
            var ended = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, AgentScripts.Key(runId))).Where(e => e.Kind == "session" && e.Detail.Contains("ended", StringComparison.Ordinal)).ToList();
            Assert.True(ended.Count >= 2 && ended.All(e => e.Detail.EndsWith("ended model_unreachable", StringComparison.Ordinal)), string.Join(", ", ended.Select(e => e.Detail)));
        }
        finally
        {
            await CleanUpAsync(h, "N1", runId, ticket);
        }
    }

    /// <summary>N2: the proxy sends a token GitHub refuses; the step does not succeed but stops for a person, a configuration fault.</summary>
    [Fact]
    public async Task With_a_key_github_refuses_the_real_copilot_step_stops_for_a_person()
    {
        await using var h = await AgentHarness.StartAsync("live-copilot-n2", null, Refused);
        var ticket = Ticket("n2");
        var runId = h.Start(ticket, "Change nothing in the repository. Write notes.md in your output folder saying hello." + Finish);
        try
        {
            await h.WaitForWaitAsync(runId, "intervention", RunTimeout);
            Assert.Contains("the model refused the step's requests", await h.TimelineAsync(runId));
            Assert.Empty(await h.RecordsAsync<ReportRecord>(Queries.Reports, AgentScripts.Key(runId)));
        }
        finally
        {
            await CleanUpAsync(h, "N2", runId, ticket);
        }
    }
}
