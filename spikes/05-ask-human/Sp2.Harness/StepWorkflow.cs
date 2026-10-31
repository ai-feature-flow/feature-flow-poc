using System.Text.Json;
using Temporalio.Activities;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Sp2;

public sealed class StepInput
{
    public string StoreDir { get; set; } = "";
    public int QuestionTimeoutSeconds { get; set; } = 7200;
}

public sealed class Question
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Status { get; set; } = "open";
    public string? Answer { get; set; }
    public bool FinalWritten { get; set; }
}

public sealed class StepState
{
    public List<Question> Questions { get; set; } = [];
    public int OpenSignals { get; set; }
    public List<ReportIn> Reports { get; set; } = [];
}

/// <summary>
/// The run's side of one agent step: the open question decision, its answer or
/// timeout, and the agent's report. The record store is written only from here.
/// </summary>
[Workflow]
public class StepWorkflow
{
    private static readonly ActivityOptions WriteOptions = new() { StartToCloseTimeout = TimeSpan.FromSeconds(30) };

    private readonly StepInput input;
    private readonly StepState state = new();

    [WorkflowInit]
    public StepWorkflow(StepInput input) => this.input = input;

    [WorkflowRun]
    public async Task<StepState> RunAsync(StepInput input)
    {
        await Workflow.WaitConditionAsync(() =>
            state.Reports.Count > 0 && state.Questions.All(q => q.FinalWritten));
        return state;
    }

    /// <summary>Idempotent by question id: a client retrying after a lost response opens nothing new.</summary>
    [WorkflowSignal(Names.OpenQuestion)]
    public Task OpenQuestionAsync(QuestionOpen open)
    {
        state.OpenSignals++;
        if (state.Questions.All(q => q.Id != open.Id))
        {
            var question = new Question { Id = open.Id, Text = open.Text };
            state.Questions.Add(question);
            _ = WatchAsync(question);
        }
        return Task.CompletedTask;
    }

    [WorkflowSignal(Names.Report)]
    public Task ReportAsync(ReportIn report)
    {
        state.Reports.Add(report);
        return Task.CompletedTask;
    }

    [WorkflowUpdateValidator(nameof(AnswerAsync))]
    public void ValidateAnswer(AnswerIn answer)
    {
        var question = state.Questions.FirstOrDefault(q => q.Id == answer.QuestionId)
            ?? throw new ApplicationFailureException($"unknown question {answer.QuestionId}");
        if (question.Status == "timed_out")
            throw new ApplicationFailureException("timed out: the agent has carried on");
        if (question.Status == "answered" && question.Answer != answer.Answer)
            throw new ApplicationFailureException($"conflict: already answered {question.Answer}");
    }

    [WorkflowUpdate(Names.Answer)]
    public Task<string> AnswerAsync(AnswerIn answer)
    {
        var question = state.Questions.First(q => q.Id == answer.QuestionId);
        if (question.Status == "answered")
            return Task.FromResult("duplicate");
        question.Status = "answered";
        question.Answer = answer.Answer;
        return Task.FromResult("recorded");
    }

    [WorkflowQuery]
    public StepState State() => state;

    private async Task WatchAsync(Question question)
    {
        var stepId = Workflow.Info.WorkflowId;
        await Workflow.ExecuteActivityAsync((StepActivities a) =>
            a.WriteRecordAsync(input.StoreDir, stepId, new QuestionRecord(question.Id, "open", null)), WriteOptions);

        var settled = await Workflow.WaitConditionAsync(() => question.Status != "open",
            TimeSpan.FromSeconds(input.QuestionTimeoutSeconds));
        if (!settled)
            question.Status = "timed_out";

        await Workflow.ExecuteActivityAsync((StepActivities a) =>
            a.WriteRecordAsync(input.StoreDir, stepId, new QuestionRecord(question.Id, question.Status, question.Answer)),
            WriteOptions);
        question.FinalWritten = true;
    }
}

public sealed class StepActivities
{
    /// <summary>Writes by atomic rename, so a replica never reads a half-written file.</summary>
    [Activity]
    public async Task WriteRecordAsync(string storeDir, string stepId, QuestionRecord record)
    {
        var path = RecordStore.PathFor(storeDir, stepId, record.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(record));
        File.Move(temp, path, overwrite: true);
    }
}
