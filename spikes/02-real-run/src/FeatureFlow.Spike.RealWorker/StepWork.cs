using FeatureFlow.Spike.CodeHost;
using Actor = FeatureFlow.Spike.Providers.Actor;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;
using System.Diagnostics;
using System.Text;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// Applies a step's work to the checkout and commits it.
///
/// <para>
/// <b>Why this exists, stated plainly.</b> The model behind this spike's agent
/// runtime is a local 7B/8B model reached through Ollama. Ollama returns a tool
/// call as prose text rather than a structured <c>tool_use</c> block, so the
/// runtime never acts on it: the agent describes the change instead of making
/// it. That is an environment limitation, not a property of the engine.
/// </para>
///
/// <para>
/// So the step's work is applied here, and everything around it is real: the
/// runtime is really started and polled, the commit is really made on the
/// branch, the diff is really read back from the code host, CI is really
/// dispatched, the PR is really opened and merged. The report names which part
/// is model-authored and which is not.
/// </para>
/// </summary>
public static class StepWork
{
    /// <summary>
    /// Applies the work for a step and commits it. Returns the new commit sha,
    /// or null when the step has no work to apply.
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
    /// The change each step makes. Spike scope: a small, real change to the
    /// fixture, so the run produces a real diff for review to look at.
    /// </summary>
    private static (string Path, string Content)? ChangeFor(string stepId) => stepId switch
    {
        "plan_spec" => ("docs/ticket-plan.md",
            "# Ticket plan\n\n" +
            "Goal: expose the number of widgets held by the store.\n" +
            "File to change: src/FeatureFlow.Example.Api/WidgetStore.cs\n" +
            "Test to add: tests/FeatureFlow.Example.Api.UnitTests/WidgetStoreTests.cs\n"),

        "design" => ("docs/ticket-plan.md",
            "## Design\n\n" +
            "`public int Count()` returns `_widgets.Count`.\n" +
            "It belongs on WidgetStore because the store owns the collection and is the\n" +
            "only type that may read it under its lock.\n"),

        "implement" => ("src/FeatureFlow.Example.Api/WidgetStore.cs",
            """
            namespace FeatureFlow.Example.Api;

            /// <summary>
            /// In-memory store. The integration tier swaps this for the Postgres-backed
            /// implementation, which is what makes the Docker-dependent tier meaningful.
            /// </summary>
            public sealed class WidgetStore
            {
                private readonly object _gate = new();
                private readonly List<Widget> _widgets = new();
                private int _nextId = 1;

                public IReadOnlyList<Widget> All()
                {
                    lock (_gate)
                    {
                        return _widgets.ToList();
                    }
                }

                public Widget? Find(int id)
                {
                    lock (_gate)
                    {
                        return _widgets.FirstOrDefault(w => w.Id == id);
                    }
                }

                public Widget Add(string name)
                {
                    lock (_gate)
                    {
                        var widget = new Widget(_nextId++, name);
                        _widgets.Add(widget);
                        return widget;
                    }
                }

                /// <summary>The number of widgets currently held by the store.</summary>
                public int Count()
                {
                    lock (_gate)
                    {
                        return _widgets.Count;
                    }
                }
            }
            """),

        "fix_findings" => ("tests/FeatureFlow.Example.Api.UnitTests/WidgetStoreTests.cs",
            """
            using FeatureFlow.Example.Api;
            using Xunit;

            namespace FeatureFlow.Example.Api.UnitTests;

            [Trait("Category", "unit")]
            public sealed class WidgetStoreTests
            {
                [Fact]
                public void Add_assigns_sequential_ids()
                {
                    var store = new WidgetStore();

                    var first = store.Add("alpha");
                    var second = store.Add("beta");

                    Assert.Equal(1, first.Id);
                    Assert.Equal(2, second.Id);
                }

                [Fact]
                public void Find_returns_null_for_unknown_id()
                {
                    var store = new WidgetStore();

                    Assert.Null(store.Find(404));
                }

                [Fact]
                public void All_reflects_added_widgets()
                {
                    var store = new WidgetStore();
                    store.Add("alpha");

                    var all = store.All();

                    Assert.Single(all);
                    Assert.Equal("alpha", all[0].Name);
                }

                /// <summary>
                /// The regression test for the Count() finding. Without it the
                /// fix.verify step fails (docs/06 10.1).
                /// </summary>
                [Fact]
                public void Count_returns_the_number_of_widgets()
                {
                    var store = new WidgetStore();
                    store.Add("alpha");
                    store.Add("beta");

                    Assert.Equal(2, store.Count());
                }
            }
            """),

        _ => null,
    };

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
