using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Integration.Tests;

/// <summary>Pieces the agent step tests share, in the Integration and Live categories.</summary>
public static class AgentScripts
{
    public static string Key(string runId) => $"{runId}/implement/1";

    public static JsonObject Report(string outcome = "implemented", string summary = "Wrote notes.md", string? answer = "none", string? notes = "notes.md")
    {
        var report = new JsonObject { ["outcome"] = outcome, ["summary"] = summary };
        if (answer is not null)
            report["outputs"] = new JsonObject { ["answer"] = answer };
        if (notes is not null)
            report["artifacts"] = new JsonObject { ["notes"] = notes };
        return report;
    }

    public static JsonObject Write(string path, string content) => new() { ["file_path"] = path, ["content"] = content };

    /// <summary>The answer the agent received: the text after "The person answered: " in a tool result.</summary>
    public static string Answer(ScriptedModel.Conversation c)
    {
        const string prefix = "The person answered: ";
        var text = c.ToolResults.LastOrDefault(r => r.Contains(prefix, StringComparison.Ordinal));
        return text is null ? "none" : text[(text.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..].Trim();
    }

    /// <summary>The value after <paramref name="prefix"/> on the last timeline line holding it, such as an artifact reference.</summary>
    public static string Reference(string timeline, string prefix)
    {
        var line = timeline.Split('\n').Last(l => l.Contains(prefix, StringComparison.Ordinal));
        return line[(line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..].Trim();
    }

    /// <summary>The step's sandbox was created once, and every later attempt re-attached to that same sandbox.</summary>
    public static async Task AssertOneSandboxAsync(AgentHarness h, string stepKey)
    {
        var sandboxes = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, stepKey)).Where(e => e.Kind == "sandbox").Select(e => e.Detail.Split(' ')).ToList();
        Assert.Single(sandboxes, s => s[0] == "created");
        Assert.Contains(sandboxes, s => s[0] == "re-attached");
        Assert.Single(sandboxes.Select(s => s[1]).Distinct());
    }
}
