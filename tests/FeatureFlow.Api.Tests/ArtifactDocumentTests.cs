using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// An artifact read as a document (A7): in its declared type's format where the run indexed it with one, else as its text
/// shows; data as JSON text, Markdown's body apart from its front matter; bytes that are not text refused with 415.
/// </summary>
public sealed class ArtifactDocumentTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Indexes <paramref name="text"/> as an artifact of the run under <paramref name="type"/>; returns its reference.</summary>
    static async Task<string> IndexedAsync(DecisionWorld world, string runId, string name, string? type, string text)
    {
        var reference = await world.PutAsync(text);
        await world.AppendAsync(new RecordBatch(Artifacts: [new ArtifactRecord(runId, "plan", 1, name, type, reference, null, false, T0)]));
        return reference;
    }

    static JsonElement Data(ArtifactDocument document) => JsonDocument.Parse(document.Data!).RootElement;

    [Fact]
    public async Task A_yaml_or_json_artifact_is_its_data_as_json()
    {
        await using var world = new DecisionWorld();
        var run = await world.StartAsync();
        var yaml = await IndexedAsync(world, run.RunId, "requirements", "std:artifact_types/requirements", "schema: 1\nrequirements:\n  - { id: R1, text: \"Widgets: listed\" }\n");
        var json = await IndexedAsync(world, run.RunId, "merge_record", "std:artifact_types/merge_record", """{ "merged": true, "count": 2 }""");
        var untypedJson = await IndexedAsync(world, run.RunId, "evidence", null, """[1, "two"]""");

        var fromYaml = await world.GetAsync<ArtifactDocument>($"runs/{run.RunId}/artifacts/{yaml}/document");
        var fromJson = await world.GetAsync<ArtifactDocument>($"runs/{run.RunId}/artifacts/{json}/document");
        var sniffed = await world.GetAsync<ArtifactDocument>($"runs/{run.RunId}/artifacts/{untypedJson}/document");

        Assert.Equal(("yaml", null), (fromYaml.Format, fromYaml.Body));
        Assert.Equal("Widgets: listed", Data(fromYaml).GetProperty("requirements")[0].GetProperty("text").GetString());
        Assert.Equal(1, Data(fromYaml).GetProperty("schema").GetInt32());
        Assert.Equal("json", fromJson.Format);
        Assert.True(Data(fromJson).GetProperty("merged").GetBoolean());
        Assert.Equal(("json", "two"), (sniffed.Format, Data(sniffed)[1].GetString()));
    }

    [Fact]
    public async Task A_markdown_artifact_is_its_body_with_its_front_matter_as_data_and_other_text_is_text()
    {
        await using var world = new DecisionWorld();
        var run = await world.StartAsync();
        var contract = await IndexedAsync(world, run.RunId, "contract", "std:artifact_types/contract", "---\ntitle: Widgets API\nchanges: [Renamed GET /w]\n---\n# Contract\n\nBody text.\n");
        var bare = await IndexedAsync(world, run.RunId, "contract2", "std:artifact_types/contract", "# Contract\n\n- not: yaml\n");
        var untyped = await IndexedAsync(world, run.RunId, "notes", null, "---\nowner: alice\n---\nNotes\n");
        var text = await IndexedAsync(world, run.RunId, "log", null, "- line one\n- line two\n");

        var withFront = await world.GetAsync<ArtifactDocument>($"runs/{run.RunId}/artifacts/{contract}/document");
        var withoutFront = await world.GetAsync<ArtifactDocument>($"runs/{run.RunId}/artifacts/{bare}/document");
        var sniffed = await world.GetAsync<ArtifactDocument>($"runs/{run.RunId}/artifacts/{untyped}/document");
        var plain = await world.GetAsync<ArtifactDocument>($"runs/{run.RunId}/artifacts/{text}/document");

        Assert.Equal(("markdown", "# Contract\n\nBody text.\n"), (withFront.Format, withFront.Body));
        Assert.Equal("Widgets API", Data(withFront).GetProperty("title").GetString());
        Assert.Equal(("markdown", null, "# Contract\n\n- not: yaml\n"), (withoutFront.Format, withoutFront.Data, withoutFront.Body));
        Assert.Equal(("markdown", "alice", "Notes\n"), (sniffed.Format, Data(sniffed).GetProperty("owner").GetString(), sniffed.Body));
        Assert.Equal(("text", null, "- line one\n- line two\n"), (plain.Format, plain.Data, plain.Body));
    }

    [Fact]
    public async Task A_binary_artifact_is_415_and_one_the_run_does_not_hold_is_404()
    {
        await using var world = new DecisionWorld();
        await world.RunAsync("r1");
        var binary = (await world.Store.PutAsync(new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xFE]), DecisionWorld.Ct)).Unwrap().Id;
        await world.AppendAsync(new RecordBatch(Artifacts: [new ArtifactRecord("r1", "build", 1, "image", null, binary, null, true, T0)]));
        var elsewhere = await world.PutAsync("schema: 1\n");

        using var refused = await world.Http.GetAsync($"runs/r1/artifacts/{binary}/document", DecisionWorld.Ct);
        using var missing = await world.Http.GetAsync($"runs/r1/artifacts/{elsewhere}/document", DecisionWorld.Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
