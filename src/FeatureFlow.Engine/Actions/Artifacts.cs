using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>An <c>artifact.import</c>'s provenance (LOG 151): the repository and exact commit, and each file with the artifact it became.</summary>
public sealed record ImportRecord(string Repository, string Commit, string Folder, IReadOnlyList<ImportedFile> Files);

/// <summary>One imported file: its path in the repository, the artifact's name and declared type, its reference, and the content's hash.</summary>
public sealed record ImportedFile(string Path, string Name, string? Type, string Ref, string Hash);

/// <summary>
/// An <c>artifact.publish</c>'s record (LOG 151), on <c>published</c> and on <c>conflict</c>: where it wrote (the repository,
/// the base branch the pull request targets, the publication branch), the commit and pull request (null on a conflict, or
/// when the files already matched the base), and each file. <see cref="Supersedes"/> is the earlier publication's pull
/// request when this one replaced it; <see cref="Conflicts"/> say why nothing was written.
/// </summary>
public sealed record Publication(string Repository, string Base, string Branch, string? Commit, string? PullRequest, IReadOnlyList<PublishedFile> Files,
    string? Supersedes, IReadOnlyList<string> Conflicts);

/// <summary>
/// One published file: its path in the repository, the artifact it came from, and content hashes (<c>sha256:</c>): what the
/// base held before (null: no such file) and what the publication wrote.
/// </summary>
public sealed record PublishedFile(string Path, string Artifact, string Ref, string? Before, string Written);

/// <summary>
/// The artifact actions (docs/04 section 12): <c>artifact.compose</c>, <c>artifact.import</c> (from a repository at an exact
/// commit; a session submission is M7's) and <c>artifact.publish</c>. Members and files are named artifacts of this run.
/// </summary>
static class Artifacts
{
    const int MaxFiles = 64;
    const int MaxFileBytes = 1024 * 1024;

    /// <summary>
    /// <c>artifact.compose</c>: groups named artifacts into an <see cref="ArtifactSet"/> at their exact references. Copies and
    /// approves nothing. The set is the artifact the step declares for <c>composed</c>.
    /// </summary>
    public static async Task<ActionResult> ComposeAsync(ActionRun run)
    {
        if (!run.Call.Contract.ArtifactsFor("composed").Any())
            throw ActionStop.CannotAnswer("artifact.compose declares no artifact to hold the set; add it to produces");
        var names = run.Strings("members");
        if (names.Count == 0)
            throw ActionStop.CannotAnswer("artifact.compose needs members: the names of the artifacts to group");
        var members = new List<ArtifactSetMember>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var entry = Latest(run, name) ?? throw ActionStop.CannotAnswer($"artifact.compose: no step of this run has produced '{name}'");
            members.Add(new ArtifactSetMember(name, entry.Type, entry.Ref));
        }

        var set = new ArtifactSet(run.String("name") ?? run.Call.Contract.ArtifactsFor("composed").First(), members);
        run.Events.Add($"composed {set.Name} of {string.Join(", ", members.Select(m => $"{m.Name} ({Short(m.Ref)})"))}");
        return run.Report("composed", evidence: await run.PutJsonAsync(set));
    }

    /// <summary>
    /// <c>artifact.import</c>: reads <c>files</c> (each <c>{ path, name, type }</c>, relative to <c>folder</c>) from
    /// <c>repository</c> at <c>commit</c>, a full commit id, so the content is pinned; registers each as the artifact it names,
    /// which the step must declare for <c>imported</c>. A YAML or JSON file must parse. Every other artifact the step declares
    /// for <c>imported</c> receives the <see cref="ImportRecord"/>. Importing is not approval (docs/01 section 5.3).
    /// </summary>
    public static async Task<ActionResult> ImportAsync(ActionRun run)
    {
        var repo = run.Repository();
        var commit = run.String("commit") ?? throw ActionStop.CannotAnswer("artifact.import needs commit: a full commit id");
        if (!IsCommit(commit))
            throw ActionStop.CannotAnswer($"artifact.import's commit must be a full commit id (40 or 64 hexadecimal characters), not '{commit}': a branch or tag can move");
        var folder = SafePath(run.String("folder") ?? "", "folder", allowEmpty: true);
        var files = Maps(run, "files");
        if (files.Count is 0 or > MaxFiles)
            throw ActionStop.CannotAnswer($"artifact.import takes 1 to {MaxFiles} files, not {files.Count}");
        var declared = run.Call.Contract.ArtifactsFor("imported").ToList();
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        var imported = new List<ImportedFile>();
        foreach (var file in files)
        {
            var path = Join(folder, SafePath(Text(file, "path"), "a file's path"));
            var name = Text(file, "name");
            if (!declared.Contains(name))
                throw ActionStop.CannotAnswer($"artifact.import: '{name}' is not an artifact the step declares for imported");
            if (artifacts.ContainsKey(name))
                throw ActionStop.CannotAnswer($"artifact.import names '{name}' twice");
            var read = ActionRun.Must(await run.CodeHost.ReadFileAsync(repo, path, commit, run.Ct), $"read {path} at {EngineActions.Short(commit)}")
                       ?? throw ActionStop.CannotAnswer($"{repo.FullName} has no file {path} at {commit}");
            if (Encoding.UTF8.GetByteCount(read.Content) > MaxFileBytes)
                throw ActionStop.CannotAnswer($"{path} is larger than {MaxFileBytes} bytes");
            if (ParseError(path, read.Content) is { } error)
                throw ActionStop.CannotAnswer($"{path} at {EngineActions.Short(commit)} does not parse: {error}");
            var reference = await PutTextAsync(run, read.Content);
            artifacts[name] = reference;
            imported.Add(new ImportedFile(path, name, Value(file, "type") as string, reference, Hash(read.Content)));
        }

        var record = await run.PutJsonAsync(new ImportRecord(repo.FullName, commit, folder, imported));
        foreach (var name in declared.Where(n => !artifacts.ContainsKey(n)))
            artifacts[name] = record;
        run.Events.Add($"imported {string.Join(", ", imported.Select(f => $"{f.Path} as {f.Name}"))} from {repo.FullName} at {EngineActions.Short(commit)}");
        var values = new Dictionary<string, string>(run.Call.Contract.ZeroOutputs, StringComparer.Ordinal);
        return ActionResult.Reported(new StepReport("imported", values, artifacts, $"{run.Call.Step.StepId}: imported"), run.Events);
    }

    /// <summary>
    /// <c>artifact.publish</c>: copies named artifacts into <c>repository</c> under <c>folder</c> (<c>files</c>, each
    /// <c>{ artifact, path }</c>) in one commit on a branch of its own, and opens a pull request into the default branch;
    /// a person merges it. With <c>previous</c> (an earlier publication's record), each path it wrote must hold what it
    /// expects: what it wrote if its pull request merged, else what was there before it. Otherwise, or if the publication
    /// branch moved under it, the outcome is <c>conflict</c> and nothing is written. An open earlier pull request is
    /// superseded in place: same branch, same pull request.
    /// </summary>
    public static async Task<ActionResult> PublishAsync(ActionRun run)
    {
        var repo = run.Repository();
        var folder = SafePath(run.String("folder") ?? "", "folder", allowEmpty: true);
        var files = Maps(run, "files");
        if (files.Count is 0 or > MaxFiles)
            throw ActionStop.CannotAnswer($"artifact.publish takes 1 to {MaxFiles} files, not {files.Count}");
        var sources = new List<(string Path, string Artifact, string Ref, string Text)>();
        foreach (var file in files)
        {
            var name = Text(file, "artifact");
            var entry = Latest(run, name) ?? throw ActionStop.CannotAnswer($"artifact.publish: no step of this run has produced '{name}'");
            var path = Join(folder, SafePath(Text(file, "path"), "a file's path"));
            if (sources.Any(s => s.Path == path))
                throw ActionStop.CannotAnswer($"artifact.publish writes {path} twice");
            sources.Add((path, name, entry.Ref, await run.ReadTextAsync(entry.Ref)));
        }

        var baseBranch = ActionRun.Must(await run.CodeHost.GetDefaultBranchAsync(repo, run.Ct), "read the default branch");
        var baseHead = ActionRun.Must(await run.Vcs.ResolveAsync(repo, "refs/heads/" + baseBranch, run.Ct), $"read {baseBranch}")
                       ?? throw ActionStop.CannotAnswer($"{repo.FullName} has no branch {baseBranch}");

        var (previous, previousPr) = await PreviousAsync(run, repo);
        var conflicts = new List<string>();
        if (previous is not null)
            foreach (var written in previous.Files)
            {
                var expected = previousPr?.State == PullRequestStates.Merged ? written.Written : written.Before;
                var now = await ContentHashAsync(run, repo, written.Path, baseHead);
                if (now != expected)
                    conflicts.Add($"{written.Path} on {baseBranch} is {now ?? "absent"}; the earlier publication expects {expected ?? "absent"}");
            }

        var supersede = previous is not null && previousPr?.State == PullRequestStates.Open;
        var branch = supersede
            ? previous!.Branch
            : $"ff/publish/{EngineActions.Safe(run.Call.Step.RunId)}/{EngineActions.Safe(run.Call.Step.StepId)}/{run.Call.Step.Execution.ToString(CultureInfo.InvariantCulture)}";
        var branchHead = ActionRun.Must(await run.Vcs.ResolveAsync(repo, "refs/heads/" + branch, run.Ct), $"read {branch}");
        var mine = (await run.EventsAsync()).LastOrDefault(e => e.Kind == CommitEvent)?.Detail;
        var expectedHead = supersede ? previous!.Commit : null;
        if (branchHead is not null && branchHead != expectedHead && branchHead != mine)
            conflicts.Add($"{branch} is at {EngineActions.Short(branchHead)}, a commit neither this step nor the earlier publication made");

        var befores = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var source in sources)
            befores[source.Path] = await ContentHashAsync(run, repo, source.Path, baseHead);
        List<PublishedFile> Published() => sources.Select(s => new PublishedFile(s.Path, s.Artifact, s.Ref, befores[s.Path], Hash(s.Text))).ToList();
        if (conflicts.Count > 0)
        {
            foreach (var c in conflicts)
                run.Events.Add("conflict: " + c);
            var conflict = new Publication(repo.FullName, baseBranch, branch, null, null, Published(), null, conflicts);
            return run.Report("conflict", evidence: await run.PutJsonAsync(conflict));
        }

        string? commit;
        if (mine is not null && branchHead == mine)
            commit = mine;
        else
        {
            await using var session = await SandboxSession.OpenAsync(run, repo, "refs/heads/" + baseBranch, baseHead, branch, [], "publish");
            foreach (var source in sources)
                await session.CopyInAsync(source.Ref, source.Path);
            commit = ActionRun.Must(await run.Vcs.CommitAllAsync(session.Copy,
                $"Publish {string.Join(", ", sources.Select(s => s.Path))}\n\nPublished by Feature Flow for run {run.Call.Step.RunId}, step {run.Call.Step.StepId}.", run.Ct),
                "commit the publication");
            if (commit is not null)
            {
                var bundle = ActionRun.Must(await run.Vcs.FetchCommitsAsync(session.Copy, baseHead, run.Ct), "take the publication's commit");
                await run.EventAsync(CommitEvent, commit);
                var pushed = await run.Vcs.PushAsync(repo, "refs/heads/" + branch, commit, bundle, branchHead, run.Ct);
                if (pushed is { Ok: false, Error.Reason: "base_moved" })
                {
                    run.Events.Add($"conflict: {branch} moved while the publication was written");
                    var moved = new Publication(repo.FullName, baseBranch, branch, null, null, Published(), null, [$"{branch} moved while the publication was written"]);
                    return run.Report("conflict", evidence: await run.PutJsonAsync(moved));
                }

                ActionRun.Must(pushed, "push the publication");
            }
        }

        if (commit is null)
        {
            run.Events.Add($"{string.Join(", ", sources.Select(s => s.Path))} already hold this content on {baseBranch}: nothing to publish");
            var unchanged = new Publication(repo.FullName, baseBranch, branch, null, null, Published(), null, []);
            return run.Report("published", new Dictionary<string, object?> { ["pull_request"] = "", ["commit"] = "" }, await run.PutJsonAsync(unchanged));
        }

        var spec = new PullRequestSpec(run.String("title") ?? $"Publish {string.Join(", ", sources.Select(s => s.Artifact))} from {run.Call.Step.RunId}",
            run.String("body") ?? $"Published by Feature Flow: {string.Join(", ", sources.Select(s => $"`{s.Path}` from {s.Artifact}"))}.", branch, baseBranch);
        var pr = supersede
            ? ActionRun.Must(await run.CodeHost.UpdatePullRequestAsync(ActionRun.ParsePullRequest(previous!.PullRequest)!, spec, run.Ct), "update the pull request")
            : ActionRun.Must(await run.CodeHost.OpenPullRequestAsync(repo, spec, $"{run.Call.Step.RunId}/{branch}", run.Ct), "open the pull request");
        run.Events.Add($"published {sources.Count} file(s) to {repo.FullName} {branch} at {EngineActions.Short(commit)}, pull request {ActionRun.Format(pr)}" +
                       (supersede ? " (superseding the earlier publication in place)" : ""));
        var record = new Publication(repo.FullName, baseBranch, branch, commit, ActionRun.Format(pr), Published(), previous?.PullRequest, []);
        return run.Report("published", new Dictionary<string, object?> { ["pull_request"] = ActionRun.Format(pr), ["commit"] = commit },
            await run.PutJsonAsync(record));
    }

    /// <summary>The event recording the commit a publication made, so a retried step adopts it rather than writing another.</summary>
    const string CommitEvent = "publish_commit";

    static async Task<(Publication? Record, PullRequestState? PullRequest)> PreviousAsync(ActionRun run, RepoRef repo)
    {
        if (run.String("previous") is not { } name)
            return (null, null);
        // Only a publication's own record counts: an agent's artifact of the same name is never trusted (F7).
        var entry = Latest(run, name, "artifact.publish")
                    ?? throw ActionStop.CannotAnswer($"artifact.publish: previous names '{name}', which no artifact.publish step of this run produced");
        Publication? record;
        try
        {
            record = JsonSerializer.Deserialize<Publication>(await run.ReadTextAsync(entry.Ref), ActionRun.JsonOptions);
        }
        catch (JsonException e)
        {
            throw ActionStop.CannotAnswer($"the earlier publication '{name}' does not read: {e.Message}");
        }

        if (record is null || record.Repository != repo.FullName)
            throw ActionStop.CannotAnswer($"the earlier publication '{name}' is not one into {repo.FullName}");
        var state = ActionRun.ParsePullRequest(record.PullRequest) is { } pr
            ? ActionRun.Must(await run.CodeHost.GetPullRequestAsync(pr, run.Ct), "read the earlier publication's pull request")
            : null;
        return (record, state);
    }

    /// <summary>
    /// The latest artifact of a name the run holds: nearest the step's own scope, as the workflow resolves consumed
    /// artifacts. With <paramref name="action"/>, only one that action produced.
    /// </summary>
    static ArtifactEntry? Latest(ActionRun run, string name, string? action = null)
    {
        var step = run.Call.Step.StepId;
        var prefix = step.LastIndexOf('.') is var dot and > 0 ? step[..(dot + 1)] : "";
        return run.Scope.Artifacts
            .Where(a => a.Name == name && (action is null || a.Action == action))
            .OrderByDescending(a => a.StepId.StartsWith(prefix, StringComparison.Ordinal))
            .ThenBy(a => a.StepId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    static async Task<string?> ContentHashAsync(ActionRun run, RepoRef repo, string path, string revision) =>
        ActionRun.Must(await run.CodeHost.ReadFileAsync(repo, path, revision, run.Ct), $"read {path}") is { } file ? Hash(file.Content) : null;

    static async Task<string> PutTextAsync(ActionRun run, string text) =>
        ActionRun.Must(await run.Provider<IArtifactStoreProvider>("artifact_store").PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), run.Ct), "store the artifact").Id;

    static string Hash(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static string Short(string reference) => reference.Length > 17 ? reference[..17] : reference;

    static bool IsCommit(string text) => text.Length is 40 or 64 && text.All(Uri.IsHexDigit);

    /// <summary>A relative path inside the repository: no leading slash, no <c>..</c> or empty parts, nothing under <c>.git</c>.</summary>
    static string SafePath(string path, string what, bool allowEmpty = false)
    {
        var trimmed = path.Trim().TrimEnd('/');
        if (trimmed.Length == 0 && allowEmpty)
            return "";
        var parts = trimmed.Split('/');
        if (trimmed.Length == 0 || trimmed.StartsWith('/') || trimmed.Contains('\\', StringComparison.Ordinal)
            || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw ActionStop.CannotAnswer($"{what} '{path}' must be a relative path inside the repository");
        return trimmed;
    }

    static string Join(string folder, string path) => folder.Length == 0 ? path : $"{folder}/{path}";

    static List<IDictionary<string, object?>> Maps(ActionRun run, string name)
    {
        var list = new List<IDictionary<string, object?>>();
        foreach (var item in run.Inputs.GetValueOrDefault(name) as IEnumerable<object?> ?? [])
            list.Add(item as IDictionary<string, object?> ?? throw ActionStop.CannotAnswer($"each of {run.Call.What}'s {name} is a map, not {item}"));
        return list;
    }

    static object? Value(IDictionary<string, object?> map, string key) => map.TryGetValue(key, out var value) ? value : null;

    static string Text(IDictionary<string, object?> map, string key) =>
        Value(map, key) as string is { Length: > 0 } text ? text : throw ActionStop.CannotAnswer($"each file needs {key}");

    /// <summary>Why a YAML or JSON file does not parse, or null; other files are taken as they are.</summary>
    static string? ParseError(string path, string content)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".yaml" or ".yml" or ".json"))
            return null;
        var d = new DiagnosticBag();
        YamlText.Parse(content, path, d);
        return d.HasErrors ? string.Join("; ", d.Select(x => x.Message)) : null;
    }
}
