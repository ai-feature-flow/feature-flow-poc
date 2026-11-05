using System.Text.Json;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.Agent.Tests;

/// <summary>The report's contract check (docs/04 section 3.3; LOG 76, 79) and the hook policy (docs/02 section 4.1).</summary>
public sealed class ReportAndHookTests
{
    internal static readonly StepContract Contract = new(
        [new OutcomeSpec("implemented", "success"), new OutcomeSpec("blocked", "blocked")],
        new Dictionary<string, string> { ["answer"] = "string", ["count"] = "number", ["commits"] = "number" },
        [new ArtifactSpec("implemented", "notes")],
        new Dictionary<string, string> { ["answer"] = "\"\"", ["count"] = "0", ["commits"] = "0" })
    {
        Reported = ["answer", "count"],
    };

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    const string Valid = """{"outcome":"implemented","summary":"done","outputs":{"answer":"x","count":2},"artifacts":{"notes":"notes.md"}}""";

    [Fact]
    public void A_valid_report_is_accepted_with_its_outputs_typed()
    {
        var (report, errors) = ReportCheck.Check(Json(Valid), Contract);
        Assert.Empty(errors);
        Assert.Equal("implemented", report!.Outcome);
        Assert.Equal("2", report.Outputs["count"]);
        Assert.Equal("notes.md", report.Artifacts["notes"]);
    }

    [Theory]
    [InlineData("""{"outcome":"done","summary":"x"}""", "'done' is not one of this step's outcomes")]
    [InlineData("""{"outcome":"implemented","summary":"x","outputs":{"answer":"x"},"artifacts":{"notes":"n.md"}}""", "output 'count' is required")]
    [InlineData("""{"outcome":"implemented","summary":"x","outputs":{"answer":"x","count":2.5},"artifacts":{"notes":"n.md"}}""", "output 'count'")]
    [InlineData("""{"outcome":"implemented","summary":"x","outputs":{"answer":"x","count":2}}""", "must give artifact 'notes'")]
    [InlineData("""{"outcome":"implemented","summary":"x","outputs":{"answer":"x","count":2},"artifacts":{"notes":"../../etc/passwd"}}""", "relative to your output folder")]
    [InlineData("""{"outcome":"implemented","summary":"x","outputs":{"answer":"x","count":2,"commits":5},"artifacts":{"notes":"n.md"}}""", "output 'commits' is not one this step reports")]
    [InlineData("""{"outcome":"blocked","summary":"x","artifacts":{"notes":"n.md"}}""", "artifact 'notes' is not one outcome 'blocked' produces")]
    [InlineData("""{"outcome":"blocked","summary":""}""", "summary must not be empty")]
    public void An_invalid_report_is_refused_with_an_error_naming_the_fault(string text, string error)
    {
        var (report, errors) = ReportCheck.Check(Json(text), Contract);
        Assert.Null(report);
        Assert.Contains(errors, e => e.Contains(error, StringComparison.Ordinal));
    }

    [Fact]
    public void A_summary_over_2_KB_is_refused() =>
        Assert.Contains(ReportCheck.Check(Json($$"""{"outcome":"blocked","summary":"{{new string('x', 2049)}}"}"""), Contract).Errors, e => e.Contains("at most 2048", StringComparison.Ordinal));

    [Fact]
    public void An_artifact_the_sandbox_client_found_missing_is_refused() =>
        Assert.Contains(ReportCheck.Check(Json(Valid), Contract, ["notes.md"]).Errors, e => e.Contains("does not exist", StringComparison.Ordinal));

    [Fact]
    public void A_blocked_report_needs_no_outputs() => Assert.Empty(ReportCheck.Check(Json("""{"outcome":"blocked","summary":"no"}"""), Contract).Errors);

    static readonly StepClaims Implementer = new("k", "r", "w", "s", "branch", ["read", "search", "edit", "shell"], 0);
    static readonly StepClaims Reviewer = Implementer with { WriteAccess = "none", Tools = ["read", "search", "shell"] };

    [Theory]
    [InlineData("git push")]
    [InlineData("cd /ff/work && git push origin HEAD")]
    [InlineData("git -C /ff/work push --force")]
    [InlineData("echo x; git push")]
    [InlineData("/usr/bin/git push")]
    [InlineData("cd /ff/work && /usr/local/bin/git -c x=y push")]
    public void A_git_push_is_denied(string command) =>
        Assert.Equal("deny", HookPolicy.PreToolUse(Implementer, "Bash", Json($$"""{"command":{{JsonSerializer.Serialize(command)}}}""")).Decision);

    [Theory]
    [InlineData("git status")]
    [InlineData("git commit -m 'push the button'")]
    [InlineData("dotnet test")]
    public void Other_commands_are_allowed(string command) =>
        Assert.Equal("allow", HookPolicy.PreToolUse(Implementer, "Bash", Json($$"""{"command":{{JsonSerializer.Serialize(command)}}}""")).Decision);

    [Fact]
    public void A_role_with_no_write_access_may_not_write() =>
        Assert.Equal("deny", HookPolicy.PreToolUse(Reviewer, "Write", Json("""{"file_path":"/ff/work/x"}""")).Decision);

    [Theory]
    [InlineData("Edit", """{"file_path":"/ff/work/.git/config"}""")]
    [InlineData("Write", """{"file_path":"/ff/work/.git/hooks/pre-commit"}""")]
    [InlineData("MultiEdit", """{"file_path":".git/config"}""")]
    [InlineData("Bash", """{"command":"echo x >> .git/config"}""")]
    [InlineData("Bash", """{"command":"cp evil .git/hooks/post-commit"}""")]
    // Copilot CLI names the file "path" (LOG 293).
    [InlineData("Edit", """{"path":"/ff/work/.git/config","old_str":"a","new_str":"b"}""")]
    [InlineData("Write", """{"path":"/ff/work/.git/hooks/pre-commit","file_text":"x"}""")]
    public void Nobody_edits_the_repository_s_git_folder(string tool, string input) =>
        Assert.Equal("deny", HookPolicy.PreToolUse(Implementer, tool, Json(input)).Decision);

    /// <summary>Copilot CLI names the engine's tools <c>engine-&lt;tool&gt;</c>, as Claude Code names them <c>mcp__engine__&lt;tool&gt;</c>: always allowed.</summary>
    [Theory]
    [InlineData("mcp__engine__report")]
    [InlineData("engine-report")]
    [InlineData("engine-ask_human")]
    public void The_engines_tools_are_allowed_under_either_runtime_s_name(string tool) =>
        Assert.Equal("allow", HookPolicy.PreToolUse(Reviewer with { Tools = [] }, tool, Json("""{"outcome":"done"}""")).Decision);

    [Fact]
    public void A_file_copilot_opens_is_recorded_from_its_path() =>
        Assert.Equal("/ff/work/a.cs", HookPolicy.FileOpened("Read", Json("""{"path":"/ff/work/a.cs"}""")));

    [Fact]
    public void A_file_whose_name_only_contains_git_may_be_edited() =>
        Assert.Equal("allow", HookPolicy.PreToolUse(Implementer, "Edit", Json("""{"file_path":"/ff/work/.gitignore"}""")).Decision);

    /// <summary>Each tool is allowed exactly when its class is one of the role's (LOG 65, LOG 101).</summary>
    [Theory]
    [InlineData("read", "Read", true)]
    [InlineData("read", "Grep", false)]
    [InlineData("search", "Grep", true)]
    [InlineData("search", "Glob", true)]
    [InlineData("search", "Bash", false)]
    [InlineData("edit", "Write", true)]
    [InlineData("edit", "Read", false)]
    [InlineData("shell", "Bash", true)]
    [InlineData("shell", "Edit", false)]
    public void A_tool_is_allowed_only_in_its_class(string toolClass, string tool, bool allowed) =>
        Assert.Equal(allowed ? "allow" : "deny",
            HookPolicy.PreToolUse(Implementer with { Tools = [toolClass] }, tool, Json("""{"file_path":"/ff/work/a.txt","command":"ls","pattern":"x"}""")).Decision);

    [Fact]
    public void A_tool_outside_the_role_s_classes_is_denied_and_the_engine_s_tools_are_allowed()
    {
        Assert.Equal("deny", HookPolicy.PreToolUse(Reviewer with { Tools = ["read"] }, "Bash", Json("""{"command":"ls"}""")).Decision);
        Assert.Equal("deny", HookPolicy.PreToolUse(Implementer, "WebFetch", Json("""{"url":"https://example.com"}""")).Decision);
        Assert.Equal("allow", HookPolicy.PreToolUse(Reviewer, "mcp__engine__report", Json("{}")).Decision);
    }
}
