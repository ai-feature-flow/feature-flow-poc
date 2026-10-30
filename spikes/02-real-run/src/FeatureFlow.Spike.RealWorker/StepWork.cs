using FeatureFlow.Spike.CodeHost;
using Actor = FeatureFlow.Spike.Providers.Actor;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;
using System.Diagnostics;
using System.Text;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// Applies an <b>orchestration</b> step's work to the checkout and commits it.
///
/// <para>
/// <b>Review 14 A3 changed what this is for.</b> It used to apply the content
/// of every <c>agent.run</c> step, because the local Ollama models behind the
/// runtime emitted tool calls as prose text rather than structured
/// <c>tool_use</c> blocks, so the agent described the change instead of making
/// it. The spike's first run therefore substituted canned content for the
/// provider under test — the one provider the spike most needed to prove.
/// </para>
///
/// <para>
/// That is no longer necessary: <c>z-ai/glm-5.3-flash</c> through OpenRouter
/// produces real structured tool calls, and the Copilot CLI authors real files
/// with them. So <c>agent.run</c> steps now commit only what the agent wrote,
/// and this class is used <b>only</b> for genuinely non-agent steps — the
/// orchestration the engine performs itself, such as seeding the fixture's
/// ticket plan before a run.
/// </para>
///
/// <para>
/// <see cref="RealActivities.RunAgentAsync"/> no longer calls this. The
/// assertion in <c>Review14Checks</c> keeps it that way.
/// </para>
/// </summary>
public static class StepWork
{
    /// <summary>
    /// Applies the work for an orchestration step and commits it. Returns the
    /// new commit sha, or null when the step has no work to apply.
    /// </summary>
    public static async Task<string?> ApplyAsync(RunContext context, Step step)
    {
        var change = ChangeFor(step.Id);
        if (change is null)
            return null;

        var (path, content) = change.Value;

        var full = Path.Combine(context.WorkingDirectory, path.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (path.EndsWith(".md", StringComparison.Ordinal) && File.Exists(full))
        {
            // Append, so a later step extends an earlier step's document rather
            // than overwriting it.
            File.AppendAllText(full, Environment.NewLine + content + Environment.NewLine);
        }
        else
        {
            File.WriteAllText(full, content);
        }

        // A real commit on the branch, with the idempotency marker in the
        // trailer (S4), so a retry adopts rather than duplicates.
        var message = $"{context.TicketId}: {step.Id}\n\n{Marker.Trailer(context.IdempotencyKey(step.Id, context.Branch))}";

        await GitAsync(context, "add", path);
        await GitAsync(context, "commit", "-m", message);

        var head = await HeadAsync(context);

        // Push, so the code host sees the commit. The push credential comes from
        // the broker, never from configuration (docs/04 section 1, floor F2).
        await PushAsync(context);

        return head;
    }

    /// <summary>
    /// Pushes the branch using a credential from the broker. The token goes into
    /// the remote URL for one command only; it is never written to the
    /// repository's config or to a file.
    /// </summary>
    private static async Task<bool> PushAsync(RunContext context)
    {
        var credential = await context.Broker.IssueAsync(
            Actor.Engine("step-work"),
            GitHubAppCredentialBroker.TargetSystemName,
            CredentialScope.ForRepository(
                $"{RunContext.Owner}/{RunContext.Repository}", "contents:write", "contents:read").Text,
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        if (!credential.IsOk)
        {
            Console.WriteLine($"STEP push: credential refused: " +
                              $"{credential.Error!.Class}/{credential.Error.Reason}");
            return false;
        }

        var token = credential.Value!.Secret;
        var remote = $"https://x-access-token:{token}@github.com/{RunContext.Owner}/{RunContext.Repository}.git";

        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = context.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("push");
        startInfo.ArgumentList.Add(remote);
        startInfo.ArgumentList.Add(context.Branch);

        using var git = Process.Start(startInfo);
        if (git is null)
            return false;

        await git.WaitForExitAsync();

        if (git.ExitCode != 0)
        {
            var error = await git.StandardError.ReadToEndAsync();
            Console.WriteLine($"STEP push: exited {git.ExitCode}: {error.Trim()}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// The change an <b>orchestration</b> step makes.
    ///
    /// <para>
    /// Review 14 A3: the four <c>agent.run</c> steps — <c>plan_spec</c>,
    /// <c>design</c>, <c>implement</c> and <c>fix_findings</c> — are deliberately
    /// absent. Their content is the agent's to write. A step id that is not
    /// listed here gets no canned content, so adding one back is a visible
    /// change to this file rather than an invisible substitution at run time.
    /// </para>
    /// </summary>
    private static (string Path, string Content)? ChangeFor(string stepId) => stepId switch
    {
        // No agent.run step appears here. See the header.
        _ => null,
    };

    /// <summary>
    /// Whether a step has canned content. Review 14 A3 asserts this is false for
    /// every <c>agent.run</c> step, so a substitution cannot come back silently.
    /// </summary>
    public static bool HasChangeFor(string stepId) => ChangeFor(stepId) is not null;


    private static async Task<string?> HeadAsync(RunContext context)
    {
        var output = await GitAsync(context, "rev-parse", "HEAD");
        return output?.Trim();
    }

    private static async Task<string?> GitAsync(RunContext context, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = context.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var git = Process.Start(startInfo);
        if (git is null)
            return null;

        var stdout = await git.StandardOutput.ReadToEndAsync();
        await git.WaitForExitAsync();

        return git.ExitCode == 0 ? stdout : null;
    }
}
