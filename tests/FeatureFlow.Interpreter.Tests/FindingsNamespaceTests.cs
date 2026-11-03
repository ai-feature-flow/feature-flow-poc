using System.Text.Json;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// The <c>findings</c> namespace in the core (docs/03 section 5.3): the counts a findings action reports reach the merge
/// bar's guards, and survive the hand-over to a fresh Temporal run (continue-as-new), which carries the core's state.
/// </summary>
public class FindingsNamespaceTests
{
    static readonly LibrarySet Std = new(Library.Library.Open(Probe.StdRoot, new DiagnosticBag())!);
    static readonly LibraryReference Ticket = new("std", null, "templates", "ticket_delivery");
    static readonly RunInfo Run = new("t-1", ["acme/api"], new Dictionary<string, YNode> { ["ticket_id"] = new YScalar("T-1", true, new SourceLocation("t", 1, 1)) });

    /// <summary>Drives std's ticket to its review's recorders, reporting <paramref name="openBlocking"/> with them, and returns the step the merge bar chose.</summary>
    static (RunCore Core, string Next) ToMergeBar(long openBlocking, bool handOver)
    {
        var (core, d) = RunCore.Start(Std, Ticket, Run);
        Assert.True(core is not null, string.Join("\n", d));
        var queue = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            foreach (var command in core!.Advance())
                if (command is StartStep s)
                    queue.Add(s.StepId);
                else if (command is OpenDecision o)
                    queue.Add(o.StepId);
            if (queue.FirstOrDefault(s => !s.StartsWith("verify.review", StringComparison.Ordinal) && s.StartsWith("verify.", StringComparison.Ordinal) && s != "verify.tests" && s != "verify.analysis") is { } after)
                return (core, after);
            var id = queue[0];
            queue.RemoveAt(0);
            var step = core.StepById(id)!;
            if (step.Form == Model.Plans.StepForm.Decision)
            {
                core.Apply(new DecisionAnswered(id, step.DeclaredOutcomes.First().Name));
                continue;
            }

            if (step.Action == "findings.record")
            {
                core.Apply(new FindingsChanged(new Dictionary<string, long> { ["open_blocking"] = openBlocking }));
                if (handOver)
                {
                    core.Flush();
                    core = RunCore.Resume(Std, Ticket, JsonSerializer.Deserialize<CarriedCore>(JsonSerializer.Serialize(core.Snapshot()))!);
                }
            }

            var outcome = step.Outcomes().First(o => o.Class == OutcomeClass.Success).Name;
            var artifacts = step.Produces.Where(p => p.Outcome is null || p.Outcome == outcome).ToDictionary(p => p.Name, p => $"sha256:{id}/{p.Name}");
            var result = core.Apply(new StepCompleted(id, outcome, RunCore.ZeroOutputs(step), artifacts));
            Assert.True(result.Accepted, string.Join("; ", result.Errors));
        }

        throw new InvalidOperationException("the merge bar was not reached");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_open_blocking_finding_sends_the_merge_bar_to_the_fix_loop_and_none_lets_it_through(bool handOver)
    {
        Assert.Equal("verify.fix.triage", ToMergeBar(1, handOver).Next);
        Assert.Equal("verify.satisfaction", ToMergeBar(0, handOver).Next);
    }

    [Fact]
    public void The_counts_are_carried_over_a_hand_over()
    {
        var (core, _) = RunCore.Start(Std, Ticket, Run);
        core!.Apply(new FindingsChanged(new Dictionary<string, long> { ["open_blocking"] = 2, ["repeat_fixes"] = 1 }));

        var resumed = RunCore.Resume(Std, Ticket, JsonSerializer.Deserialize<CarriedCore>(JsonSerializer.Serialize(core.Snapshot()))!);

        Assert.Equal((2L, 1L), (resumed.State.Findings["open_blocking"], resumed.State.Findings["repeat_fixes"]));
    }
}
