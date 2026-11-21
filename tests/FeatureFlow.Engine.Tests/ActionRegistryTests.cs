using FeatureFlow.Engine.Actions;
using FeatureFlow.Interpreter;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Validation;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// The action descriptor registry (docs/04 section 2; F10): an action is one registry entry, its descriptor and its handler
/// together (<see cref="EngineActions.Register"/>), which loading, validation, run start, the core, the shell's step call and
/// dispatch all read (LOG 357).
/// </summary>
public sealed class ActionRegistryTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly ActionDescriptor Probe = new("test.probe", [new("done", OutcomeClass.Success), new("failed", OutcomeClass.Failure)],
        [new ActionOutput("count", "number")], [new ActionInput("size", "string")], ["probe_provider"]);

    static string Template(string with) => $$"""
        api_version: feature-flow/v1
        kind: template
        metadata: { name: probing, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: probe
            action: test.probe
            with: {{with}}
            routes:
              done: { end: completed }
        """;

    static (LibrarySet Libraries, LibraryReference Template) Probing(string with)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "registry-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        File.WriteAllText(Path.Combine(root, "library.yaml"), "name: probe\nversion: 0.1.0\n");
        File.WriteAllText(Path.Combine(root, "templates", "probing.yaml"), Template(with));
        var d = new DiagnosticBag();
        var libraries = new LibrarySet(Library.Library.Open(Path.Combine(Repository.Root, "library", "std"), d)!, Library.Library.Open(root, d)!);
        return (libraries, new LibraryReference("probe", "templates", "probing"));
    }

    static RunInfo Run => new("run-1", ["acme/api"], new Dictionary<string, YNode>());

    [Fact]
    public async Task A_test_action_needs_one_registry_entry_and_nothing_else()
    {
        using var entry = EngineActions.Register(Probe, run => Task.FromResult(run.Report("done", new Dictionary<string, object?> { ["count"] = 3L })));

        // Load: an input of its schema loads; one outside it is refused at load (check 31).
        var (refusedLibraries, refusedTemplate) = Probing("{ colour: red }");
        var (refused, diagnostics) = RunCore.Start(refusedLibraries, refusedTemplate, Run);
        Assert.Null(refused);
        Assert.Contains(diagnostics, d => d.Check == Checks.StepInputs && d.Message.Contains("test.probe has no input 'colour'", StringComparison.Ordinal));

        var (libraries, template) = Probing("{ size: big }");
        var (core, errors) = RunCore.Start(libraries, template, Run);
        Assert.True(core is not null, string.Join("\n", errors));

        // Run start: its capabilities are what check 21 asks for.
        Assert.Equal("step probe", RunStartChecks.Needs(core.Plan, libraries).GetValueOrDefault("probe_provider"));

        // The core starts it and specifies its call as the shell does (StartStepAsync, CallOf), the engine dispatches it to its
        // handler, and the core takes its output as a number and routes its outcome.
        Assert.Equal([new StartStep("probe", 1)], core.Advance());
        var (spec, error) = core.StepSpec("probe");
        Assert.True(error is null, error);
        Assert.Equal(StepActivity.Engine, spec!.Activity);
        var call = RunWorkflow.CallOf(spec, new StepIdentity("run-1", "probing", "probe", 1, core.StepKey("probe", 1)), false, null);
        var providers = new ProviderSet(new Dictionary<string, IReadOnlyDictionary<string, IProvider>>(), new Dictionary<string, string>(), new Dictionary<string, string>());
        var result = await new EngineActions(providers).RunAsync(call, ActionScope.None, () => { }, Ct);

        var report = Assert.IsType<StepReport>(result.Report);
        var applied = core.Apply(new StepCompleted("probe", report.Outcome, report.Outputs.ToDictionary(kv => kv.Key, kv => YamlWriter.ReadPlain(kv.Value))));
        Assert.True(applied.Accepted, string.Join("; ", applied.Errors));
        core.Advance();
        Assert.Equal(RunStatus.Completed, core.State.Status);
        Assert.Equal(3L, core.State.Steps["probe"].Current!.Outputs["count"]);
    }

    [Fact]
    public void Every_action_the_engine_carries_out_has_a_handler_and_no_handler_is_for_anything_else()
    {
        var engine = ActionRegistry.Default.All.Where(d => d.Handling == ActionHandling.Engine).Select(d => d.Name).Order(StringComparer.Ordinal);

        Assert.Equal(engine, EngineActions.Handled.Order(StringComparer.Ordinal));
    }
}
