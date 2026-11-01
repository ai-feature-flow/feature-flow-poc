namespace FeatureFlow.Architecture.Tests;

/// <summary>
/// The reference rules of docs/11 section 2.3. A project not named here is a violation, so a new
/// project must be placed in the table before it builds green.
/// </summary>
public static class ReferenceRules
{
    /// <summary>The pure core. Each may reference only those before it.</summary>
    public static readonly IReadOnlyList<string> Core =
    [
        "FeatureFlow.Model",
        "FeatureFlow.Expressions",
        "FeatureFlow.Library",
        "FeatureFlow.Validation",
        "FeatureFlow.Interpreter",
    ];

    public static readonly IReadOnlyList<string> Providers =
    [
        "FeatureFlow.Providers.Abstractions",
        "FeatureFlow.Providers.Contracts",
        "FeatureFlow.Providers.Local",
        "FeatureFlow.Providers.Git",
        "FeatureFlow.Providers.GitHub",
        "FeatureFlow.Providers.Docker",
        "FeatureFlow.Providers.ClaudeCode",
        "FeatureFlow.Providers.CopilotCli",
        "FeatureFlow.Providers.StdValidators",
        "FeatureFlow.Providers.Slack",
        "FeatureFlow.Providers.Jira",
        "FeatureFlow.Providers.Postgres",
        "FeatureFlow.Providers.Kubernetes",
    ];

    /// <summary>The Temporal side: the core, provider interfaces and Temporal.</summary>
    public static readonly IReadOnlyList<string> Engine =
    [
        "FeatureFlow.Engine",
        "FeatureFlow.Engine.Agent",
        "FeatureFlow.Api",
    ];

    public static readonly IReadOnlyList<string> Hosts =
    [
        "FeatureFlow.Host.Api",
        "FeatureFlow.Host.OrchestrationWorker",
        "FeatureFlow.Host.AgentWorker",
    ];

    /// <summary>Projects section 2.3 lists without a reference rule.</summary>
    public static readonly IReadOnlyList<string> Unconstrained =
    [
        "FeatureFlow.Testing.Workflows",
        "FeatureFlow.SandboxClient",
        "FeatureFlow.Hosting",
        "FeatureFlow.Cli",
    ];

    public static IEnumerable<string> AllProjects => Core.Concat(Providers).Concat(Engine).Concat(Hosts).Concat(Unconstrained);

    const string Abstractions = "FeatureFlow.Providers.Abstractions";

    public static IEnumerable<string> Violations(string project, IEnumerable<string> projectReferences, IEnumerable<string> packageReferences)
    {
        var projects = projectReferences.ToList();
        var packages = packageReferences.ToList();

        if (Core.Contains(project))
        {
            var allowed = Core.Take(Core.IndexOf(project)).ToHashSet();
            foreach (var reference in projects.Where(r => !allowed.Contains(r)))
                yield return $"{project} references {reference}: the core may reference only core projects before it";
            foreach (var package in packages.Where(IsTemporal))
                yield return $"{project} references package {package}: the core has no Temporal reference";
        }
        else if (Providers.Contains(project))
        {
            string[] allowed = [Abstractions, "FeatureFlow.Model"];
            foreach (var reference in projects.Where(r => !allowed.Contains(r)))
                yield return $"{project} references {reference}: a provider project may reference only Providers.Abstractions and Model";
        }
        else if (Engine.Contains(project))
        {
            var allowed = Core.Append(Abstractions).ToHashSet();
            foreach (var reference in projects.Where(r => !allowed.Contains(r)))
                yield return $"{project} references {reference}: it may reference only the core, Providers.Abstractions and Temporal";
        }
        else if (Hosts.Contains(project))
        {
            var allowed = Providers.Except([Abstractions, "FeatureFlow.Providers.Contracts"]).Append("FeatureFlow.Hosting").ToHashSet();
            foreach (var reference in projects.Where(r => !allowed.Contains(r)))
                yield return $"{project} references {reference}: a host may reference only Hosting and provider projects";
        }
        else if (!Unconstrained.Contains(project))
        {
            yield return $"{project} is not in the reference rule table";
        }
    }

    static bool IsTemporal(string package) =>
        package.Equals("Temporalio", StringComparison.OrdinalIgnoreCase)
        || package.StartsWith("Temporalio.", StringComparison.OrdinalIgnoreCase);

    static int IndexOf(this IReadOnlyList<string> list, string item) => list.ToList().IndexOf(item);
}
