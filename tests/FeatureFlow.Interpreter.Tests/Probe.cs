using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// A throwaway library named <c>probe</c> holding test templates, loaded beside std so the templates can use
/// std's roles, tasks, prompts, artifact types and decision types. It is written under the test output folder
/// and rewritten on every run.
/// </summary>
public sealed class Probe
{
    public static string RepositoryRoot { get; } = FindRoot();

    public static string StdRoot => Path.Combine(RepositoryRoot, "library", "std");

    public Probe(string key, params (string Name, string Yaml)[] templates)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "probe-" + key);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        File.WriteAllText(Path.Combine(root, "library.yaml"), "name: probe\nversion: 0.1.0\n");
        foreach (var (name, yaml) in templates)
            File.WriteAllText(Path.Combine(root, "templates", name + ".yaml"), yaml);
        var d = new DiagnosticBag();
        Libraries = new LibrarySet(Library.Library.Open(StdRoot, d)!, Library.Library.Open(root, d)!);
    }

    public LibrarySet Libraries { get; }

    public static LibraryReference Ref(string name) => new("probe", null, "templates", name);

    public static RunInfo Run(params string[] repositories) =>
        new("run-1", repositories.Length == 0 ? ["acme/api"] : repositories, new Dictionary<string, YNode>());

    /// <summary>Starts a run; the template must load with no blocking diagnostic.</summary>
    public RunCore Start(string name, RunOptions? options = null, RunInfo? run = null)
    {
        var (core, diagnostics) = RunCore.Start(Libraries, Ref(name), run ?? Run(), options);
        Assert.True(core is not null, string.Join("\n", diagnostics));
        return core!;
    }

    /// <summary>Loads a template whatever its diagnostics, for running plans the load checks would refuse.</summary>
    public (Plan Plan, DiagnosticBag Diagnostics) LoadUnchecked(string name)
    {
        var d = new DiagnosticBag();
        var plan = PlanLoader.LoadTemplate(Libraries, Ref(name), d)!;
        return (plan, d);
    }

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("FeatureFlow.slnx not found above the test output folder");
    }
}

/// <summary>
/// Drives a core the way a shell does: carries out commands, and answers each started step or opened decision
/// in the order they were issued. <see cref="Respond"/> gives the input for a step, or null to leave it running.
/// </summary>
public sealed class Driver(RunCore core)
{
    public List<Command> Commands { get; } = [];
    public List<(string StepId, int Execution, bool Decision)> Queue { get; } = [];

    public Func<string, int, RunInput?> Respond { get; set; } = (id, _) => null;

    /// <summary>Advances once and records the commands, without answering anything.</summary>
    public IReadOnlyList<Command> Advance()
    {
        var commands = core.Advance();
        Commands.AddRange(commands);
        foreach (var command in commands)
            switch (command)
            {
                case StartStep s:
                    Queue.Add((s.StepId, s.Execution, false));
                    break;
                case OpenDecision o:
                    Queue.Add((o.StepId, o.Execution, true));
                    break;
                case CancelStep c:
                    Queue.RemoveAll(q => q.StepId == c.StepId);
                    break;
            }

        return commands;
    }

    /// <summary>Answers queued work until nothing is left or the run ends.</summary>
    public RunCore Run(int max = 1000)
    {
        Advance();
        for (var i = 0; i < max && Queue.Count > 0 && core.State.Status == RunStatus.Running; i++)
        {
            var (id, execution, _) = Queue[0];
            Queue.RemoveAt(0);
            if (Respond(id, execution) is not { } input)
                continue;
            var result = core.Apply(input);
            Assert.True(result.Accepted, string.Join("; ", result.Errors));
            Advance();
        }

        return core;
    }

    public static List<string> Path(RunCore core) => core.State.Trace.Where(t => t.Kind == "started").Select(t => t.StepId).ToList();
}
