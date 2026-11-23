using System.Text.Json;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Console.Tests;

/// <summary>What Needs me shows besides open waits: an answer a person gave earlier, as the API records it.</summary>
public sealed partial class FakeEngine
{
    /// <summary>
    /// Records <paramref name="actor"/>'s answer (an outcome) to a wait as <c>POST /runs/{id}/answers</c> records one the run
    /// took (the answer's arguments as JSON, the run's reply), <paramref name="ago"/> before now.
    /// </summary>
    public Task AnsweredAsync(string runId, string waitId, string outcome, string? reason = null, string actor = ConsoleWorld.Actor, TimeSpan? ago = null)
    {
        var args = new AnswerArgs(waitId, actor, Outcome: outcome, Reason: reason);
        return AppendAsync(new RecordBatch(Answers: [new AnswerRecord(runId, waitId, actor, JsonSerializer.Serialize(args, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            "recorded", DateTimeOffset.UtcNow - (ago ?? TimeSpan.FromMinutes(5)), reason)]));
    }
}
