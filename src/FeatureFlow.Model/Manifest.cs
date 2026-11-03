using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model;

/// <summary>Where a test tier's results are and how they are parsed: <c>trx</c> or <c>junit</c>, at a path that may hold a <c>*</c>.</summary>
public sealed record TierResult(string Format, string Path);

/// <summary>A test tier delegated to CI: the CI instance and the job (for GitHub Actions, the workflow file).</summary>
public sealed record TierCi(string? Instance, string Job);

/// <summary>
/// One test tier of a repository (docs/07 section 4): templates select it by tag, never by name. <see cref="Targets"/>
/// says where it may run: <c>sandbox</c>, <c>local</c>, <c>ci</c>.
/// </summary>
public sealed record TestTier(
    string Name,
    IReadOnlyList<string> Tags,
    string Command,
    TierResult? Result,
    IReadOnlyList<string> Targets,
    TierCi? Ci,
    IReadOnlyList<string> PrerequisiteEnvironment);

/// <summary>The static analyser a repository declares: <c>sarif</c> (with its command and output), <c>sonarcloud</c> or <c>none</c>.</summary>
public sealed record StaticAnalysis(string Analyser, string? Command, string? SarifPath);

/// <summary>How <c>test.integration</c> starts the repository as a service.</summary>
public sealed record IntegrationEnvironment(string? Image, string? Start, string? Health, long? Port, string? ReadyTimeout);

/// <summary>
/// The repository manifest, <c>feature-flow.yaml</c> (docs/07 section 4), read fresh from the branch at every step and
/// recorded by <see cref="Hash"/> (RW11). <see cref="Library"/> is kept as written for check 20, and is read only from
/// the default branch's copy.
/// </summary>
public sealed record RepositoryManifest(
    string Hash,
    IReadOnlyList<string> Setup,
    IReadOnlyList<string> AfterChanges,
    IReadOnlyList<TestTier> Tiers,
    StaticAnalysis? Analysis,
    IntegrationEnvironment? Integration,
    YNode? Library)
{
    /// <summary>The repository's manifest file name, at its root.</summary>
    public const string FileName = "feature-flow.yaml";

    /// <summary>Tiers with any of <paramref name="tags"/>; every tier when none is given.</summary>
    public IReadOnlyList<TestTier> TiersTagged(IReadOnlyCollection<string> tags) =>
        tags.Count == 0 ? Tiers : Tiers.Where(t => t.Tags.Any(tags.Contains)).ToList();

    /// <summary>Check 33: the tags that match no tier.</summary>
    public IReadOnlyList<string> UnknownTags(IEnumerable<string> tags) =>
        tags.Where(tag => !Tiers.Any(t => t.Tags.Contains(tag))).Distinct().ToList();

    /// <summary>
    /// The one reader every step uses (engine actions and the agent step): the manifest from a file's content and hash,
    /// or <see cref="FormatException"/> naming what is wrong, which the caller turns into an intervention.
    /// </summary>
    public static RepositoryManifest Parse(string content, string hash, string where)
    {
        var d = new DiagnosticBag();
        var manifest = Read(content, hash, where, d);
        if (manifest is null || d.HasErrors)
            throw new FormatException($"{where} does not read: {string.Join("; ", d.Where(x => x.Blocks).Select(x => x.Message))}");
        return manifest;
    }

    /// <summary>
    /// Reads a manifest, refusing unknown keys (docs/07 section 4: a misspelt section stops the step that reads it).
    /// Errors are check 1 diagnostics; null when the text is not a map.
    /// </summary>
    public static RepositoryManifest? Read(string text, string hash, string file, DiagnosticBag d)
    {
        if (MapReader.Of(YamlText.Parse(text, file, d), d, file) is not { } r)
            return null;
        var version = r.String("api_version", required: true);
        if (version is not null and not LibraryItemReader.ApiVersion)
            r.Error($"api_version must be {LibraryItemReader.ApiVersion}", r.Map.Location);
        var kind = r.String("kind", required: true);
        if (kind is not null and not "repository")
            r.Error($"kind must be repository, not '{kind}'", r.Map.Location);

        // The sandbox is not yet built from the environment section (M4 follow-up 1); it is read so it is not refused.
        if (MapReader.Of(r.Node("environment"), d, "environment") is { } env)
        {
            env.String("from");
            env.String("image");
            env.String("os");
            env.Bool("docker");
            env.Finish();
        }

        var tiers = new List<TestTier>();
        foreach (var item in LibraryItemReader.Seq(r.Node("test_tiers"), d, "test_tiers"))
            if (MapReader.Of(item, d, "a test tier") is { } t)
            {
                var name = t.String("name", required: true) ?? "";
                TierResult? result = null;
                if (MapReader.Of(t.Node("result"), d, $"test_tiers.{name}.result") is { } res)
                {
                    var format = res.Enum("format", ["trx", "junit"], required: true);
                    var path = res.String("path", required: true);
                    res.Finish();
                    if (format is not null && path is not null)
                        result = new TierResult(format, path);
                }

                TierCi? ci = null;
                if (MapReader.Of(t.Node("ci"), d, $"test_tiers.{name}.ci") is { } c)
                {
                    var job = c.String("job", required: true);
                    ci = job is null ? null : new TierCi(c.String("instance"), job);
                    c.Finish();
                }

                IReadOnlyList<string> env2 = [];
                if (MapReader.Of(t.Node("prerequisites"), d, $"test_tiers.{name}.prerequisites") is { } p)
                {
                    env2 = p.StringList("env");
                    p.Finish();
                }

                var targets = t.Has("targets") ? t.StringList("targets") : ["sandbox"];
                foreach (var target in targets.Where(x => x is not ("sandbox" or "local" or "ci")))
                    t.Error($"test_tiers.{name}.targets: '{target}' is not sandbox, local or ci", t.Map.Location);
                var command = t.String("command") ?? "";
                if (targets.Contains("ci") && ci is null && !targets.Contains("sandbox"))
                    t.Error($"test_tiers.{name} runs only in ci and needs ci.job", t.Map.Location);
                if (command.Length == 0 && targets.Contains("sandbox"))
                    t.Error($"test_tiers.{name} runs in a sandbox and needs a command", t.Map.Location);
                tiers.Add(new TestTier(name, t.StringList("tags"), command, result, targets, ci, env2));
                t.Finish();
            }

        foreach (var duplicate in tiers.GroupBy(t => t.Name).Where(g => g.Count() > 1))
            r.Error($"test tier '{duplicate.Key}' is declared twice", r.Map.Location);

        StaticAnalysis? analysis = null;
        if (MapReader.Of(r.Node("static_analysis"), d, "static_analysis") is { } s)
        {
            var analyser = s.Enum("analyser", ["sarif", "sonarcloud", "none"], required: true) ?? "none";
            analysis = new StaticAnalysis(analyser, s.String("command"), s.String("sarif_path"));
            if (analyser == "sarif" && (analysis.Command is null || analysis.SarifPath is null))
                s.Error("static_analysis with analyser sarif needs command and sarif_path", s.Map.Location);
            s.Finish();
        }

        IntegrationEnvironment? integration = null;
        if (MapReader.Of(r.Node("integration_environment"), d, "integration_environment") is { } i)
        {
            integration = new IntegrationEnvironment(i.String("image"), i.String("start"), i.String("health"), i.Int("port"), i.String("ready_timeout"));
            i.Finish();
        }

        var manifest = new RepositoryManifest(hash, Commands(r, "setup", d), Commands(r, "after_changes", d), tiers, analysis, integration, r.Node("library"));
        r.Finish();
        return manifest;
    }

    static IReadOnlyList<string> Commands(MapReader r, string section, DiagnosticBag d)
    {
        if (MapReader.Of(r.Node(section), d, section) is not { } s)
            return [];
        var commands = s.StringList("commands");
        s.Finish();
        return commands;
    }
}

/// <summary>Repository patterns such as <c>acme/poc-*</c> (F1's <c>engine_may_merge</c>): <c>*</c> matches any run of characters.</summary>
public static class RepositoryPatterns
{
    public static bool Matches(string pattern, string repository)
    {
        var parts = pattern.Split('*');
        if (parts.Length == 1)
            return string.Equals(pattern, repository, StringComparison.OrdinalIgnoreCase);
        var at = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i == 0)
            {
                if (!repository.StartsWith(part, StringComparison.OrdinalIgnoreCase))
                    return false;
                at = part.Length;
                continue;
            }

            if (i == parts.Length - 1)
                return repository.Length - at >= part.Length && repository.EndsWith(part, StringComparison.OrdinalIgnoreCase);
            var found = repository.IndexOf(part, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return false;
            at = found + part.Length;
        }

        return true;
    }

    public static bool Any(IEnumerable<string> patterns, string repository) => patterns.Any(p => Matches(p, repository));
}
