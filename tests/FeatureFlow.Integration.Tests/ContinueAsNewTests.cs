using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// SP1's scenario (docs/spikes/sp1-results.md, spikes/04-continue-as-new) rewritten against this interpreter:
/// a parent with child runs, decisions open from the start and a chain of actions continues-as-new many times;
/// children end and answers arrive during hand-overs, and none is lost. The checks are SP1's, with its
/// thresholds scaled to one run. Its negative controls (a child signalling the parent's run id; the default
/// parent-close policy) make this test fail; tools/check_m3_controls.py runs them.
/// </summary>
[Trait("Category", "Integration")]
public class ContinueAsNewTests
{
    const int Seed = 1;
    const int Children = 24;
    const int Questions = 10;
    const int WorkItems = 12;
    static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);

    const string Script = """
        { "sp1_child:wait": { delay_from: wait_ms, outcome_from: outcome }, "sp1_parent:*": { delay_from: wait_ms } }
        """;

    [Fact]
    public async Task Children_ending_and_answers_arriving_during_hand_overs_are_not_lost()
    {
        var random = new Random(Seed);
        var children = Enumerable.Range(0, Children).Select(_ => (WaitMs: random.Next(5_000, 40_001), Outcome: random.Next(4) == 0 ? "gave_up" : "done")).ToList();
        var work = Enumerable.Range(0, WorkItems).Select(_ => random.Next(3_000, 6_001)).ToList();
        var answers = Enumerable.Range(0, Questions).Select(_ => random.Next(2) == 0 ? "yes" : "no").ToList();
        var answerDelays = Enumerable.Range(0, Questions).Select(_ => random.Next(1_000, 45_001)).ToList();

        await using var h = await Harness.StartAsync("sp1", Script, libraries: [Path.Combine(Harness.TestLibraries, "sp1")]);
        var id = h.Start("sp1:templates/sp1_parent",
            "--param", "children=[" + string.Join(", ", children.Select(c => $"{{ wait_ms: {c.WaitMs}, outcome: {c.Outcome} }}")) + "]",
            "--param", "questions=[" + string.Join(", ", Enumerable.Range(0, Questions)) + "]",
            "--param", "work=[" + string.Join(", ", work) + "]",
            "--repo", "acme/api", "--continue-as-new-events", "100");
        var handle = h.Client.GetWorkflowHandle(id);

        // The decisions open at the start; find each question's wait id from the record store, through the API.
        var waits = new Dictionary<int, string>();
        var until = DateTime.UtcNow.AddSeconds(60);
        while (waits.Count < Questions && DateTime.UtcNow < until)
        {
            foreach (var wait in (await h.ShowAsync(id))["waits"]!.AsArray())
                if (wait!["stepId"]!.ToString() is var step && step.StartsWith("questions_", StringComparison.Ordinal))
                    waits[int.Parse(step["questions_".Length..], CultureInfo.InvariantCulture)] = wait["waitId"]!.ToString();
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.True(waits.Count == Questions, (await h.ShowAsync(id)).ToJsonString()[..Math.Min(3000, (await h.ShowAsync(id)).ToJsonString().Length)]);

        // Even questions are answered through the API after a seeded delay; odd ones by an Update sent the
        // moment the run reports it is draining for a hand-over (SP1 sent half that way).
        var sentDuringDrain = new ConcurrentBag<int>();
        var answering = Enumerable.Range(0, Questions).Select(q => Task.Run(async () =>
        {
            if (q % 2 == 0)
            {
                await Task.Delay(answerDelays[q]);
                var (exit, output, error) = h.Cli("answer", id, waits[q], answers[q]);
                Assert.True(exit == 0 && output.Trim() == "recorded", $"question {q}: {output}{error}");
                return;
            }

            var giveUp = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < giveUp)
            {
                var view = await handle.QueryAsync<RunView>(RunWorkflowNames.View, []);
                if (view.Draining)
                {
                    await handle.ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Answer, [new AnswerArgs(waits[q], "harness", answers[q])]);
                    sentDuringDrain.Add(q);
                    return;
                }

                await Task.Delay(20);
            }

            await handle.ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Answer, [new AnswerArgs(waits[q], "harness", answers[q])]);
        })).ToList();
        await Task.WhenAll(answering);

        // Repeats of an answer are acknowledged without a second record; a different answer is refused.
        foreach (var q in new[] { 0, 1, 2 })
            Assert.Equal("duplicate", h.Cli("answer", id, waits[q], answers[q]).Output.Trim());
        foreach (var q in new[] { 3, 4 })
        {
            var (exit, _, error) = h.Cli("answer", id, waits[q], answers[q] == "yes" ? "no" : "yes");
            Assert.Equal(1, exit);
            Assert.Contains("already answered", error);
        }

        // C4: the run ends within the deadline. It is partial when any child gave up, otherwise completed.
        var (endExit, end, _) = h.Cli("wait", id, "--timeout", Deadline.TotalSeconds.ToString(CultureInfo.InvariantCulture));
        var final = await handle.QueryAsync<RunView>(RunWorkflowNames.View, []);
        var record = await h.ShowAsync(id);
        var expected = children.Any(c => c.Outcome == "gave_up") ? "partial" : "completed";
        Assert.True(end.Trim() == $"run {id} {expected}",
            $"C4: the run did not end {expected} within {Deadline}: {end} {final.Status}; open waits {string.Join(", ", final.Waits.Select(w => w.StepId))}; running {string.Join(", ", final.InFlight)}; unconfirmed child ends {final.Children.Count}");

        var timeline = record["timeline"]!.AsArray().Select(e => e!).ToList();
        List<string> Completions(string step) =>
            timeline.Where(e => e["kind"]!.ToString() == "completed" && e["stepId"]!.ToString() == step).Select(e => e["detail"]!.ToString()).ToList();

        // C1: every child's end recorded once, with the outcome Temporal reports for that child when read directly.
        for (var i = 0; i < Children; i++)
        {
            var childId = $"{id}.children_{i}.1";
            var description = await h.Client.GetWorkflowHandle(childId).DescribeAsync();
            Assert.Equal(Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Completed, description.Status);
            var status = await h.Client.GetWorkflowHandle(childId).GetResultAsync<string>();
            Assert.Equal(children[i].Outcome == "done" ? "completed" : "failed", status);
            Assert.Equal([RunCore.ChildOutcome(RunStatuses.Parse(status)!.Value)], Completions($"children_{i}"));
        }

        // C2: every answer recorded once, as sent; the conflicting answers did not replace it.
        for (var q = 0; q < Questions; q++)
            Assert.Equal([answers[q]], Completions($"questions_{q}"));

        // C3: nothing started while draining, and no activity in flight at any hand-over.
        Assert.Equal(0, final.ActivitiesStartedWhileDraining);
        Assert.All(final.HandOvers, o => Assert.Equal(0, o.InFlightActivities));

        // C5: no workflow task failed in any run.
        Assert.Equal(0, await h.WorkflowTaskFailuresAsync(id));

        // Exercised: without these the checks above prove nothing (SP1's E1 to E3).
        var withBoth = final.HandOvers.Count(o => o.RunningChildren > 0 && o.OpenWaits > 0);
        Assert.True(withBoth >= 3, $"E1: {withBoth} hand-over(s) with children running and decisions open; at least 3 needed: {string.Join("; ", final.HandOvers)}");
        Assert.True(final.ChildEndsWhileDraining >= 1, $"E2: no child end arrived while draining ({final.HandOvers.Count} hand-overs)");
        Assert.True(!sentDuringDrain.IsEmpty && final.AnswersWhileDraining >= 1,
            $"E3: {sentDuringDrain.Count} answer(s) sent and {final.AnswersWhileDraining} received while draining");

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"hand-overs {final.HandOvers.Count}, with children and decisions {withBoth}, child ends while draining {final.ChildEndsWhileDraining}, answers while draining {final.AnswersWhileDraining}");
        await h.RecordHistoryAsync(id, "sp1_parent");
        Assert.True(await h.RecordResumedHistoryAsync(id, "sp1_parent_resumed"), "no run resumed carrying unconfirmed child ends");
    }
}
