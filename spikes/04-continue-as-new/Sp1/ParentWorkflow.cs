using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Sp1;

/// <summary>
/// A parent run under the continue-as-new rule of docs/02 section 3: drain
/// activities; do not wait for children or open decisions, which are carried as
/// state.
/// </summary>
[Workflow]
public class ParentWorkflow
{
    private const int Concurrency = 4;

    private static readonly ActivityOptions WorkOptions = new() { StartToCloseTimeout = TimeSpan.FromMinutes(1) };

    private readonly ParentState state;

    // Transient: belongs to this run only.
    private readonly HashSet<string> confirmsStarted = [];
    private bool draining;
    private int inFlight;
    private bool dirty;

    // Signals and updates can arrive before the run method starts, so the state is
    // set here rather than in RunAsync.
    [WorkflowInit]
    public ParentWorkflow(ParentState input) => state = input;

    private int Run => state.HandOvers.Count;

    [WorkflowRun]
    public async Task<string> RunAsync(ParentState input)
    {
        if (state.FirstRunId is null)
            await InitialiseAsync();

        while (true)
        {
            dirty = false;
            if (!draining && Workflow.CurrentHistoryLength >= state.HistoryThreshold)
                draining = true;
            if (draining)
                await HandOverAsync();

            if (Done())
                return "completed";

            await StartWorkAsync();
            await Workflow.WaitConditionAsync(() => dirty);
        }
    }

    [WorkflowSignal]
    public Task ChildEndedAsync(string childId)
    {
        state.SignalsReceived++;
        if (draining)
            state.SignalsWhileDraining++;
        var child = state.Children.FirstOrDefault(c => c.Id == childId);
        if (child is { Ended: false } && !state.PendingConfirms.Contains(childId))
            state.PendingConfirms.Add(childId);
        dirty = true;
        return Task.CompletedTask;
    }

    [WorkflowUpdateValidator(nameof(AnswerAsync))]
    public void ValidateAnswer(Answer answer)
    {
        var decision = state.Decisions.FirstOrDefault(d => d.Id == answer.DecisionId)
            ?? throw new ApplicationFailureException($"unknown decision {answer.DecisionId}");
        if (draining && state.Mode != Modes.NoStale)
            throw new ApplicationFailureException("stale: workflow is continuing-as-new");
        if (decision.Answer is not null && decision.Answer != answer.Outcome)
            throw new ApplicationFailureException($"conflict: already answered {decision.Answer}");
    }

    [WorkflowUpdate]
    public Task<string> AnswerAsync(Answer answer)
    {
        var decision = state.Decisions.First(d => d.Id == answer.DecisionId);
        if (decision.Answer == answer.Outcome)
        {
            state.DuplicateAnswersAcked++;
            return Task.FromResult("duplicate");
        }
        decision.Answer = answer.Outcome;
        state.AnswerLog.Add(new AnswerRecord(answer.DecisionId, answer.Outcome, Run));
        dirty = true;
        return Task.FromResult("recorded");
    }

    [WorkflowQuery]
    public ParentView View() => new(state, draining, inFlight, Workflow.Info.RunId);

    private async Task InitialiseAsync()
    {
        state.FirstRunId = Workflow.Info.RunId;
        var random = new Random(state.Seed);
        state.WorkMillis = Enumerable.Range(0, 300).Select(_ => random.Next(200, 1501)).ToList();
        state.Children = Enumerable.Range(0, 20).Select(i => new ChildPlan
        {
            Id = $"{Workflow.Info.WorkflowId}-child-{i:00}",
            Seconds = random.Next(5, 61),
            Outcome = random.Next(2) == 0 ? "completed" : "failed",
            Wave = i < 10 ? 1 : 2,
        }).ToList();
        state.Decisions = Enumerable.Range(0, 20).Select(i => new Decision { Id = $"d_{i:00}" }).ToList();
        await StartChildrenAsync(wave: 1);
    }

    private async Task StartWorkAsync()
    {
        if (state.NextWork >= state.WorkMillis.Count / 2)
            await StartChildrenAsync(wave: 2);

        foreach (var id in state.PendingConfirms.Where(id => !confirmsStarted.Contains(id)).ToList())
        {
            confirmsStarted.Add(id);
            _ = ConfirmAsync(id);
        }

        while (inFlight < Concurrency && state.NextWork < state.WorkMillis.Count)
            _ = WorkAsync(state.WorkMillis[state.NextWork++]);
    }

    private async Task StartChildrenAsync(int wave)
    {
        foreach (var child in state.Children.Where(c => c.Wave == wave && !c.Started))
        {
            var input = new ChildInput(child.Id, child.Seconds, child.Outcome, Workflow.Info.WorkflowId,
                state.Mode == Modes.SignalRunId ? state.FirstRunId : null);
            await Workflow.StartChildWorkflowAsync((ChildWorkflow w) => w.RunAsync(input), new()
            {
                Id = child.Id,
                ParentClosePolicy = state.Mode == Modes.Terminate ? ParentClosePolicy.Terminate : ParentClosePolicy.Abandon,
            });
            child.Started = true;
        }
    }

    private async Task WorkAsync(int millis)
    {
        BeginActivity();
        await Workflow.ExecuteActivityAsync((Activities a) => a.WorkAsync(millis), WorkOptions);
        EndActivity();
    }

    /// <summary>The signal is a hint; the read of the child decides.</summary>
    private async Task ConfirmAsync(string childId)
    {
        BeginActivity();
        var read = await Workflow.ExecuteActivityAsync((Activities a) => a.ReadChildAsync(childId), WorkOptions);
        EndActivity();

        if (read.Status == "Running")
        {
            // Not ended yet after all; try again on the next pass.
            confirmsStarted.Remove(childId);
            return;
        }
        var child = state.Children.First(c => c.Id == childId);
        child.EndRecords++;
        if (!child.Ended)
        {
            child.Ended = true;
            child.RecordedOutcome = $"{read.Status}:{read.Result}";
            child.EndedInRun = Run;
        }
        state.PendingConfirms.Remove(childId);
    }

    private void BeginActivity()
    {
        if (draining)
            state.StartsWhileDraining++;
        inFlight++;
        state.ActivitiesStarted++;
    }

    private void EndActivity()
    {
        inFlight--;
        state.ActivitiesCompleted++;
        dirty = true;
    }

    private async Task HandOverAsync()
    {
        await Workflow.WaitConditionAsync(() => inFlight == 0);
        await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);

        state.HandOvers.Add(new HandOver(
            Run,
            Workflow.CurrentHistoryLength,
            inFlight,
            state.Children.Count(c => c.Started && !c.Ended),
            state.Decisions.Count(d => d.Answer is null),
            state.PendingConfirms.Count));

        if (state.Mode == Modes.DropAnswers)
        {
            foreach (var decision in state.Decisions)
                decision.Answer = null;
        }

        throw Workflow.CreateContinueAsNewException((ParentWorkflow w) => w.RunAsync(state));
    }

    private bool Done() =>
        state.NextWork >= state.WorkMillis.Count &&
        inFlight == 0 &&
        state.Children.All(c => c.Ended) &&
        state.Decisions.All(d => d.Answer is not null);
}
