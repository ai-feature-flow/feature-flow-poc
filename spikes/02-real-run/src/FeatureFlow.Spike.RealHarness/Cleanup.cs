using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Leaves the fixture repository as it was found: deletes the branches and pull
/// requests created during verification.
///
/// <para>
/// The fixture is a public repository owned by the project, so verification must
/// not leave debris behind.
/// </para>
/// </summary>
public static class Cleanup
{
    public static async Task<int> RunAsync(string[] args)
    {
        HarnessProgram.WriteHeader("Cleanup: leave the fixture repository as it was found");

        var branch = HarnessProgram.Arg(args, "--branch") ?? "ff/ticket/spike-2-1";
        var baseBranch = HarnessProgram.Arg(args, "--base") ?? "ff/epic/spike-2";
        var runId = HarnessProgram.Arg(args, "--run-id") ?? $"cleanup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        var plan = PlanLoader.LoadTicketDelivery("TICKET-SPIKE2-1");
        var context = new RunContext(
            runId, plan, HarnessProgram.Fixture, branch, baseBranch,
            "TICKET-SPIKE2-1", HarnessProgram.Records);

        var failures = 0;

        // Close any pull request this run opened.
        var prNumber = Findings.LastPrNumber(context);
        if (prNumber is not null)
        {
            var closed = await context.CodeHost.ClosePullRequestAsync(
                new FeatureFlow.Spike.CodeHost.PullRequestRef(context.CodeHostRepo, prNumber.Value),
                CancellationToken.None);

            Console.WriteLine($"PR #{prNumber}: closed={closed.IsOk} " +
                              $"{(closed.Error is null ? "" : $"({closed.Error.Class}/{closed.Error.Reason})")}");
            if (!closed.IsOk && closed.Error!.Reason != "not_found")
                failures++;
        }
        else
        {
            Console.WriteLine("PR: none was opened by this run");
        }

        // Delete the branches created during verification.
        var branches = new List<string> { branch };
        var extra = HarnessProgram.Arg(args, "--branch-name");
        if (extra is not null)
            branches.Add(extra);

        foreach (var name in branches.Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(name, "main", StringComparison.Ordinal))
            {
                Console.WriteLine($"branch {name}: left alone (the repository default)");
                continue;
            }

            var deleted = await context.CodeHost.DeleteBranchAsync(
                context.CodeHostRepo, name, CancellationToken.None);

            Console.WriteLine($"branch {name}: deleted={deleted.IsOk} " +
                              $"{(deleted.Error is null ? "" : $"({deleted.Error.Class}/{deleted.Error.Reason})")}");

            // Deleting a branch that is already gone is success (docs/05 4.1).
            if (!deleted.IsOk && deleted.Error!.Reason != "not_found")
                failures++;
        }

        // Report what is left, so the repository's state is visible.
        Console.WriteLine();
        var remaining = await context.CodeHost.FindByMarkerAsync(
            context.CodeHostRepo, FeatureFlow.Spike.CodeHost.Marker.Prefix, CancellationToken.None);

        if (remaining.IsOk)
        {
            Console.WriteLine($"branches still carrying a feature-flow marker: " +
                              $"{remaining.Value!.Branches.Count}");
            foreach (var b in remaining.Value!.Branches)
                Console.WriteLine($"  {b.Name}");

            Console.WriteLine($"pull requests still carrying a marker: " +
                              $"{remaining.Value!.PullRequests.Count}");
            foreach (var p in remaining.Value!.PullRequests)
                Console.WriteLine($"  #{p.Number}");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "cleanup complete" : $"cleanup: {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }
}
