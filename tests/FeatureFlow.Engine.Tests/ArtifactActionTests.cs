using System.Text;
using System.Text.Json;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Engine.Tests;

/// <summary><c>artifact.compose</c>, <c>artifact.import</c> and <c>artifact.publish</c> (docs/04 section 12) over the in-memory world.</summary>
public sealed class ArtifactActionTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    static string Outcome(ActionResult r) => r.Report?.Outcome ?? $"{r.Kind}: {r.Reason}";

    static async Task<string> PutAsync(ActionWorld w, string text) => (await w.Store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), Ct)).Unwrap().Id;

    /// <summary>An artifact an earlier step of the run produced; a step run again replaces what it produced before.</summary>
    static async Task<string> ProducedAsync(ActionWorld w, string step, string name, string text, string? type = null)
    {
        var reference = await PutAsync(w, text);
        w.Artifacts.RemoveAll(a => a.StepId == step && a.Name == name);
        w.Artifacts.Add(new ArtifactEntry(step, name, type, reference));
        return reference;
    }

    static async Task<T> ReadAsync<T>(ActionWorld w, ActionResult r, string name) => JsonSerializer.Deserialize<T>(await w.ReadAsync(r.Report!.Artifacts[name]), Web)!;

    // ---- artifact.compose ----

    [Fact]
    public async Task Compose_groups_members_at_their_exact_references()
    {
        await using var w = new ActionWorld();
        var requirements = await ProducedAsync(w, "design", "requirements", "schema: 1\n", "std:artifact_types/requirements");
        var contract = await ProducedAsync(w, "design", "contract", "# Contract\n", "std:artifact_types/contract");

        var result = await w.RunAsync("artifact.compose", "{ members: [requirements, contract] }", "bundle", produces: [new ArtifactSpec(null, "design_set")]);

        Assert.Equal("composed", Outcome(result));
        var set = await ReadAsync<ArtifactSet>(w, result, "design_set");
        Assert.Equal("design_set", set.Name);
        Assert.Equal([("requirements", requirements), ("contract", contract)], set.Members.Select(m => (m.Name, m.Ref)));
        Assert.Equal("std:artifact_types/requirements", set.Members[0].Type);
    }

    [Fact]
    public async Task Compose_with_a_member_no_step_produced_stops_for_a_person()
    {
        await using var w = new ActionWorld();

        var result = await w.RunAsync("artifact.compose", "{ members: [missing] }", "bundle", produces: [new ArtifactSpec(null, "set")]);

        Assert.Equal("cannot_answer", result.Kind);
        Assert.Contains("'missing'", result.Reason, StringComparison.Ordinal);
    }

    // ---- artifact.import ----

    [Fact]
    public async Task Import_registers_files_at_an_exact_commit_with_their_provenance()
    {
        await using var w = new ActionWorld();
        var commit = w.CommitMain("plans/p1/requirements.yaml", "schema: 1\nitems: []\n");
        w.CommitMain("plans/p1/requirements.yaml", "changed: later\n");

        var result = await w.RunAsync("artifact.import",
            $"{{ repository: acme/api, folder: plans/p1, commit: {commit}, files: [{{ path: requirements.yaml, name: requirements, type: std:artifact_types/requirements }}] }}",
            "import", produces: [new ArtifactSpec(null, "requirements"), new ArtifactSpec(null, "provenance")]);

        Assert.Equal("imported", Outcome(result));
        Assert.Equal("schema: 1\nitems: []\n", await w.ReadAsync(result.Report!.Artifacts["requirements"]));
        var record = await ReadAsync<ImportRecord>(w, result, "provenance");
        Assert.Equal((commit, "plans/p1/requirements.yaml", "requirements"), (record.Commit, record.Files.Single().Path, record.Files.Single().Name));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("abc123")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    public async Task Import_refuses_a_commit_that_is_not_a_full_commit_id(string commit)
    {
        await using var w = new ActionWorld();

        var result = await w.RunAsync("artifact.import", $"{{ commit: {commit}, files: [{{ path: README.md, name: readme }}] }}", "import",
            produces: [new ArtifactSpec(null, "readme")]);

        Assert.Equal("cannot_answer", result.Kind);
        Assert.Contains("full commit id", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_refuses_a_file_that_does_not_parse_or_is_not_declared_or_escapes_the_repository()
    {
        await using var w = new ActionWorld();
        var commit = w.CommitMain("bad.yaml", "a: [unclosed\n");
        IReadOnlyList<ArtifactSpec> declared = [new ArtifactSpec(null, "plan")];

        var unparsed = await w.RunAsync("artifact.import", $"{{ commit: {commit}, files: [{{ path: bad.yaml, name: plan }}] }}", "a", produces: declared);
        var undeclared = await w.RunAsync("artifact.import", $"{{ commit: {commit}, files: [{{ path: README.md, name: other }}] }}", "b", produces: declared);
        var escaping = await w.RunAsync("artifact.import", $"{{ commit: {commit}, files: [{{ path: ../secrets, name: plan }}] }}", "c", produces: declared);

        Assert.Contains("does not parse", unparsed.Reason, StringComparison.Ordinal);
        Assert.Contains("not an artifact the step declares", undeclared.Reason, StringComparison.Ordinal);
        Assert.Contains("relative path inside the repository", escaping.Reason, StringComparison.Ordinal);
    }

    // ---- artifact.publish ----

    // A step declares its record for the success outcome and, separately, for conflict (class blocked).
    static readonly IReadOnlyList<ArtifactSpec> Receipt = [new ArtifactSpec(null, "receipt"), new ArtifactSpec("conflict", "receipt")];

    const string Files = "[{ artifact: contract, path: architecture.md }]";

    static PullRequestRef Pr(ActionResult r) => ActionRunAccess.Pr((string)YamlWriter.ReadPlain(r.Report!.Outputs["pull_request"])!);

    [Fact]
    public async Task Publish_writes_one_commit_on_its_own_branch_and_opens_a_pull_request_into_the_default_branch()
    {
        await using var w = new ActionWorld();
        await ProducedAsync(w, "design", "contract", "# Contract v1\n");
        var main = w.Main;

        var result = await w.RunAsync("artifact.publish", $"{{ folder: plans/p1, files: {Files} }}", "publish", produces: Receipt);

        Assert.Equal("published", Outcome(result));
        var record = await ReadAsync<Publication>(w, result, "receipt");
        Assert.Equal(main, w.Main);
        Assert.Equal([main], w.Forge.GetCommit(w.Repo, record.Commit!).Parents);
        Assert.Equal("# Contract v1\n", w.Forge.ReadFile(w.Repo, record.Commit!, "plans/p1/architecture.md"));
        var pr = (await new InMemoryCodeHost(w.Forge).GetPullRequestAsync(Pr(result), Ct)).Unwrap();
        Assert.Equal(("main", record.Branch, PullRequestStates.Open), (pr.Base, pr.Head, pr.State));
        Assert.Null(record.Files.Single().Before);
    }

    [Fact]
    public async Task A_retried_publication_adopts_its_commit_and_pull_request()
    {
        await using var w = new ActionWorld();
        await ProducedAsync(w, "design", "contract", "# Contract v1\n");
        var call = w.Call("artifact.publish", "publish", 1, $"{{ files: {Files} }}", Receipt);

        var first = await w.Actions.RunAsync(call, w.Scope, () => { }, Ct);
        var sandboxes = w.Sandbox.Counts.Created;
        var retried = await w.Actions.RunAsync(call, w.Scope, () => { }, Ct);

        Assert.Equal(("published", "published"), (Outcome(first), Outcome(retried)));
        // The retry writes nothing: memory commits are named by their content, so only the sandbox count shows a second write.
        Assert.Equal(sandboxes, w.Sandbox.Counts.Created);
        Assert.Equal(first.Report!.Outputs["commit"], retried.Report!.Outputs["commit"]);
        Assert.Equal(Pr(first), Pr(retried));
    }

    [Fact]
    public async Task Republishing_over_an_open_pull_request_supersedes_it_in_place()
    {
        await using var w = new ActionWorld();
        await ProducedAsync(w, "design", "contract", "# Contract v1\n");
        var first = await w.RunAsync("artifact.publish", $"{{ files: {Files} }}", "publish", produces: Receipt);
        await ProducedAsync(w, "design", "contract", "# Contract v2\n");

        var second = await w.RunAsync("artifact.publish", $"{{ files: {Files}, previous: receipt }}", "publish_again", produces: Receipt);

        Assert.Equal("published", Outcome(second));
        var (one, two) = (await ReadAsync<Publication>(w, first, "receipt"), await ReadAsync<Publication>(w, second, "receipt"));
        Assert.Equal((one.Branch, one.PullRequest), (two.Branch, two.PullRequest));
        Assert.Equal(one.PullRequest, two.Supersedes);
        Assert.Equal("# Contract v2\n", w.Forge.ReadFile(w.Repo, "refs/heads/" + two.Branch, "architecture.md"));
    }

    [Fact]
    public async Task A_destination_changed_since_a_merged_publication_is_a_conflict_and_nothing_is_written()
    {
        await using var w = new ActionWorld();
        await ProducedAsync(w, "design", "contract", "# Contract v1\n");
        var first = await w.RunAsync("artifact.publish", $"{{ files: {Files} }}", "publish", produces: Receipt);
        w.Forge.MergePullRequest(Pr(first));
        w.CommitMain("architecture.md", "# Someone else's edit\n");
        await ProducedAsync(w, "design", "contract", "# Contract v2\n");
        var main = w.Main;

        var second = await w.RunAsync("artifact.publish", $"{{ files: {Files}, previous: receipt }}", "publish_again", produces: Receipt);

        Assert.Equal("conflict", Outcome(second));
        var record = await ReadAsync<Publication>(w, second, "receipt");
        Assert.Null(record.Commit);
        Assert.Contains("architecture.md", record.Conflicts.Single(), StringComparison.Ordinal);
        Assert.Null(w.Forge.Resolve(w.Repo, "refs/heads/" + record.Branch));
        Assert.Equal(main, w.Main);
    }

    [Fact]
    public async Task A_destination_unchanged_since_a_merged_publication_is_published_on_a_new_branch()
    {
        await using var w = new ActionWorld();
        await ProducedAsync(w, "design", "contract", "# Contract v1\n");
        var first = await w.RunAsync("artifact.publish", $"{{ files: {Files} }}", "publish", produces: Receipt);
        w.Forge.MergePullRequest(Pr(first));
        await ProducedAsync(w, "design", "contract", "# Contract v2\n");

        var second = await w.RunAsync("artifact.publish", $"{{ files: {Files}, previous: receipt }}", "publish_again", produces: Receipt);

        Assert.Equal("published", Outcome(second));
        Assert.NotEqual(Pr(first), Pr(second));
        Assert.Equal("sha256:", (await ReadAsync<Publication>(w, second, "receipt")).Files.Single().Before![..7]);
    }

    [Fact]
    public async Task A_publication_branch_someone_else_moved_is_a_conflict()
    {
        await using var w = new ActionWorld();
        await ProducedAsync(w, "design", "contract", "# Contract v1\n");
        var first = await w.RunAsync("artifact.publish", $"{{ files: {Files} }}", "publish", produces: Receipt);
        var branch = (await ReadAsync<Publication>(w, first, "receipt")).Branch;
        var moved = w.Forge.Commit(w.Repo, "refs/heads/" + branch, new Dictionary<string, string?> { ["architecture.md"] = "# Hand edit\n" });

        var second = await w.RunAsync("artifact.publish", $"{{ files: {Files}, previous: receipt }}", "publish_again", produces: Receipt);

        Assert.Equal("conflict", Outcome(second));
        Assert.Equal(moved, w.Forge.Resolve(w.Repo, "refs/heads/" + branch));
    }

    [Fact]
    public async Task A_new_publication_branch_already_at_the_earlier_publications_commit_is_a_conflict()
    {
        await using var w = new ActionWorld();
        await ProducedAsync(w, "design", "contract", "# Contract v1\n");
        var first = await w.RunAsync("artifact.publish", $"{{ files: {Files} }}", "publish", produces: Receipt);
        var earlier = await ReadAsync<Publication>(w, first, "receipt");
        // The earlier pull request was closed, so this publication goes on a branch of its own, which must be new.
        (await new InMemoryCodeHost(w.Forge).ClosePullRequestAsync(Pr(first), null, Ct)).Unwrap();
        w.Forge.SetReference(w.Repo, $"refs/heads/ff/publish/{w.RunId}/publish_again/1", earlier.Commit);
        await ProducedAsync(w, "design", "contract", "# Contract v2\n");

        var second = await w.RunAsync("artifact.publish", $"{{ files: {Files}, previous: receipt }}", "publish_again", produces: Receipt);

        Assert.Equal("conflict", Outcome(second));
    }

    [Fact]
    public async Task Previous_must_be_a_publications_own_record()
    {
        await using var w = new ActionWorld();
        await ProducedAsync(w, "design", "contract", "# Contract\n");
        await ProducedAsync(w, "agent", "receipt", "{ \"repository\": \"acme/api\" }");

        var result = await w.RunAsync("artifact.publish", $"{{ files: {Files}, previous: receipt }}", "publish", produces: Receipt);

        Assert.Equal("cannot_answer", result.Kind);
        Assert.Contains("no artifact.publish step", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_refuses_a_path_outside_the_repository()
    {
        await using var w = new ActionWorld();
        await ProducedAsync(w, "design", "contract", "# Contract\n");

        var result = await w.RunAsync("artifact.publish", "{ files: [{ artifact: contract, path: .git/hooks/pre-commit }] }", "publish", produces: Receipt);

        Assert.Equal("cannot_answer", result.Kind);
    }
}
