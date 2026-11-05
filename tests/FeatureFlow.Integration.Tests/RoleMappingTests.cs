using System.Text.Json.Nodes;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// <c>role_mapping</c> (LOG 267): each agent step's runtime instance and model follow its role, fixed when the run starts.
/// Two Claude Code instances with their own models (<c>haiku</c>, <see cref="StrongModel"/>), and a mapping that sends
/// implementers to the first with <see cref="ImplementerModel"/> and reviewers to the second with no model, so they run on
/// that instance's own; the scripted model records the model of each request it is sent.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RoleMappingTests
{
    const string ImplementerModel = "claude-haiku-4-5";
    const string StrongModel = "opus";

    [Fact]
    public async Task A_reviewer_and_an_implementer_run_on_the_runtime_and_model_of_their_roles()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        // The script is by turn, the same for both steps' conversations, so each turn's tool call is built afresh.
        model.Then(_ => ScriptedModel.ToolUse("Bash", new JsonObject { ["command"] = "echo done > /ff/out/notes.md", ["description"] = "scripted" }))
            .Then(_ => ScriptedModel.ToolUse("mcp__engine__report", AgentScripts.Report()))
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("roles", model, new AgentHarnessOptions
        {
            MoreRuntimes = [$"claude_strong: {{ provider: claude_code, model: {StrongModel}, model_access: {{model_access}} }}"],
            MoreOrganisation = $$"""
                role_mapping:
                  implementer: { agent_runtime: claude_code, model: {{ImplementerModel}} }
                  reviewer: { agent_runtime: claude_strong }
                """,
        });

        var implementer = h.Start("T-41", "Implement the role mapping ticket.");
        Assert.Equal("completed", await h.WaitForEndAsync(implementer, TimeSpan.FromMinutes(4)));
        var reviewer = h.Start("T-42", "Review the role mapping ticket.", "m4:templates/review_step");
        Assert.Equal("completed", await h.WaitForEndAsync(reviewer, TimeSpan.FromMinutes(4)));

        string ModelOf(string instruction) => string.Join(",", model.Requests.Where(r => new ScriptedModel.Conversation(r).Prompt.Contains(instruction, StringComparison.Ordinal))
            .Select(r => r["model"]?.ToString()).Distinct());
        Assert.Equal(ImplementerModel, ModelOf("Implement the role mapping ticket."));
        // No model in the reviewer's entry: the claude_strong instance's own model shows it ran on that instance.
        // Claude Code resolves the alias to its current Opus model.
        Assert.StartsWith("claude-opus-", ModelOf("Review the role mapping ticket."), StringComparison.Ordinal);
        // Each step ran on its own runtime instance, and the run's record names it with the model.
        Assert.Contains($"session 1 started on runtime claude_code with model {ImplementerModel}", await h.TimelineAsync(implementer));
        Assert.Contains("session 1 started on runtime claude_strong with model of the runtime", await h.TimelineAsync(reviewer));
    }
}
