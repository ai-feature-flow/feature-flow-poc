using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>A run's artifacts: its index, an artifact's bytes, an artifact read as a document, and a transcript read as turns.</summary>
    static void MapArtifacts(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/artifacts", (HttpContext c, string id) => api.Guard(c, caller => api.ListArtifactsAsync(c, caller, id)))
            .Described<ArtifactList>("listArtifacts", 200, 404);
        app.MapGet("/runs/{id}/artifacts/{reference}", (HttpContext c, string id, string reference) => api.Guard(c, caller => api.ArtifactAsync(c, caller, id, reference)))
            .WithName("getArtifact").Produces<Stream>(200, "application/octet-stream").Produces<ErrorResponse>(401).Produces<ErrorResponse>(404);
        app.MapGet("/runs/{id}/artifacts/{reference}/document",
                (HttpContext c, string id, string reference) => api.Guard(c, caller => api.ArtifactDocumentAsync(c, caller, id, reference)))
            .Described<ArtifactDocument>("getArtifactDocument", 200, 404, 415);
        app.MapGet("/runs/{id}/artifacts/{reference}/turns",
                (HttpContext c, string id, string reference) => api.Guard(c, caller => api.ArtifactTurnsAsync(c, caller, id, reference)))
            .Described<TranscriptTurns>("getArtifactTurns", 200, 404, 415);
    }

    sealed partial class Handlers
    {
        /// <summary>
        /// The run's own artifact index, in the order recorded, for a viewer of the run: declared artifacts, evidence, agent
        /// sessions' transcripts and prompts, and plan revisions (LOG 325 as amended by LOG 343 and 393). A child's are
        /// listed through the child, whose scope may differ.
        /// </summary>
        public async Task<IResult> ListArtifactsAsync(HttpContext context, Caller caller, string id)
        {
            if (await VisibleRunAsync(caller, id, context.RequestAborted) is null)
                return Error(404, $"no run {id}");
            var indexed = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = id }, context.RequestAborted)).Unwrap();
            return Results.Json(new ArtifactList(indexed.Where(a => a.RunId == id).Select(ArtifactViewOf).ToList()), Json);
        }

        /// <summary>
        /// An artifact of the run, for a viewer of the run: one its own index holds (what its timeline shows, its transcripts
        /// and prompts), or one a wait of the run names as consumed, such as the parent's contract a child's gate ratifies
        /// (LOG 394). A child's own artifacts are read through the child, whose scope may differ (review R5, F1).
        /// </summary>
        public async Task<IResult> ArtifactAsync(HttpContext context, Caller caller, string id, string reference)
        {
            var ct = context.RequestAborted;
            var (_, refused) = await ReadableArtifactAsync(caller, id, reference, ct);
            if (refused is not null)
                return refused;
            var read = await Artifacts.GetAsync(new ArtifactRef(reference), ct);
            if (!read.Ok)
                return Error(404, $"artifact {reference} cannot be read: {read.Error!.Message}");
            // Saved, never shown: a browser that navigates to an artifact neither renders nor sniffs it (review R1).
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentDisposition = "attachment";
            return Results.Stream(read.Value!, "application/octet-stream");
        }

        /// <summary>
        /// An artifact of the run read as a document (A7), under the same rule as its bytes (<see cref="ArtifactAsync"/>): in the
        /// format its declared type gives where the run indexed it with one, else as its text shows (JSON, Markdown with front
        /// matter, or text). Bytes that are not UTF-8 text are 415: download them instead.
        /// </summary>
        public async Task<IResult> ArtifactDocumentAsync(HttpContext context, Caller caller, string id, string reference)
        {
            var ct = context.RequestAborted;
            var (run, text, refused) = await ReadableTextAsync(caller, id, reference, ct);
            if (refused is not null)
                return refused;
            var declared = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap()
                .LastOrDefault(a => a.Ref == reference && a.Type is not null)?.Type;
            return Results.Json(DocumentOf(text!, await FormatOfAsync(run!, declared, ct)), Json);
        }

        /// <summary>
        /// An agent session's transcript read as turns (console A4), under the same rule as its bytes (<see cref="ArtifactAsync"/>):
        /// one shape for every runtime, read here so the runtimes' formats stay out of the console (<see cref="Transcripts"/>).
        /// Text that holds no runtime's events is 415: read it as a document instead.
        /// </summary>
        public async Task<IResult> ArtifactTurnsAsync(HttpContext context, Caller caller, string id, string reference)
        {
            var (_, text, refused) = await ReadableTextAsync(caller, id, reference, context.RequestAborted);
            if (refused is not null)
                return refused;
            return Transcripts.Read(text!) is { } turns
                ? Results.Json(turns, Json)
                : Error(415, $"artifact {reference} is not a transcript this API reads; read it as a document instead");
        }

        /// <summary>The run and the artifact's text, for a viewer of the run (<see cref="ReadableArtifactAsync"/>); 404 or, for bytes that are not text, 415.</summary>
        async Task<(RunRecord? Run, string? Text, IResult? Refused)> ReadableTextAsync(Caller caller, string id, string reference, CancellationToken ct)
        {
            var (run, refused) = await ReadableArtifactAsync(caller, id, reference, ct);
            if (refused is not null)
                return (null, null, refused);
            var read = await Artifacts.GetAsync(new ArtifactRef(reference), ct);
            if (!read.Ok)
                return (null, null, Error(404, $"artifact {reference} cannot be read: {read.Error!.Message}"));
            using var bytes = new MemoryStream();
            await using (var stream = read.Value!)
                await stream.CopyToAsync(bytes, ct);
            return TextOf(bytes.ToArray()) is { } text ? (run, text, null) : (null, null, Error(415, $"artifact {reference} is not text; download it instead"));
        }

        /// <summary>
        /// The run, if the caller views it and it holds the artifact: one its own index holds, or one a wait of the run names as
        /// consumed; else the 404.
        /// </summary>
        async Task<(RunRecord? Run, IResult? Refused)> ReadableArtifactAsync(Caller caller, string id, string reference, CancellationToken ct)
        {
            var p = new Dictionary<string, string> { ["run_id"] = id };
            if (await VisibleRunAsync(caller, id, ct) is not { } run)
                return (null, Error(404, $"no run {id}"));
            var indexed = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, p, ct)).Unwrap();
            if (!indexed.Any(a => a.RunId == id && a.Ref == reference)
                && !(await Records.QueryAsync<WaitRecord>(Queries.Waits, p, ct)).Unwrap().Any(w => w.Consumed?.Any(c => c.Ref == reference) == true))
                return (null, Error(404, $"no artifact {reference} in run {id}"));
            return (run, null);
        }

        /// <summary>
        /// The format of the artifact type <paramref name="declared"/> names (<c>yaml</c>, <c>json</c> or <c>markdown</c>), as the
        /// libraries the run locked define it; null when there is no type, or the run's libraries do not have it.
        /// </summary>
        async Task<string?> FormatOfAsync(RunRecord run, string? declared, CancellationToken ct)
        {
            if (declared is null || LibraryReference.TryParse(declared) is not { Type: "artifact_types" } type || await LockOfAsync(run, ct) is not { } lockRef)
                return null;
            var libraries = await LockedLibrariesAsync(lockRef, new DiagnosticBag(), ct);
            return libraries.Resolve(type)?.ArtifactType(type.Name)?.Format;
        }
    }

    /// <summary>Bytes as UTF-8 text, or null when they are not text: not UTF-8, or holding a NUL.</summary>
    static string? TextOf(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.Contains('\0', StringComparison.Ordinal) ? null : text.TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Text as a document: YAML or JSON as its data in JSON; Markdown as its body, with its front matter as data where it has
    /// one; anything else as text. <paramref name="format"/> is the declared type's; with none, text that is a JSON object or
    /// list is JSON, text that opens with front matter is Markdown, and the rest is text. A document that does not read in
    /// its format is text.
    /// </summary>
    static ArtifactDocument DocumentOf(string text, string? format)
    {
        switch (format)
        {
            case "json" or "yaml" when DataOf(text) is { } data:
                return new ArtifactDocument(format, data, null);
            case "markdown":
                return FrontMatterOf(text) is { } parts ? new ArtifactDocument("markdown", parts.Data, parts.Body) : new ArtifactDocument("markdown", null, text);
            case null when text.TrimStart() is ['{' or '[', ..] && JsonText(text):
                return new ArtifactDocument("json", DataOf(text), null);
            case null when FrontMatterOf(text) is { } split:
                return new ArtifactDocument("markdown", split.Data, split.Body);
            default:
                return new ArtifactDocument("text", null, text);
        }
    }

    static bool JsonText(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>YAML or JSON text as JSON text, or null when it does not parse.</summary>
    static string? DataOf(string text)
    {
        var d = new DiagnosticBag();
        return YamlText.Parse(text, "artifact", d) is { } node && !d.HasErrors ? YamlWriter.Write(node) : null;
    }

    /// <summary>A Markdown document's front matter (a YAML map between <c>---</c> lines that open it) as JSON text, and the body after it; null without one.</summary>
    static (string Data, string Body)? FrontMatterOf(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---" || Array.FindIndex(lines, 1, l => l.Trim() == "---") is not (> 0 and var close))
            return null;
        var d = new DiagnosticBag();
        return YamlText.Parse(string.Join('\n', lines[1..close]), "front matter", d) is YMap front && !d.HasErrors
            ? (YamlWriter.Write(front), string.Join('\n', lines[(close + 1)..]))
            : null;
    }

    /// <summary>A document's <c>changes</c> (LOG 508): the top-level list of a YAML or JSON document, or the front-matter key of a Markdown one; null without one.</summary>
    static IReadOnlyList<string>? ChangesOf(ArtifactDocument document)
    {
        if (document.Data is not { } data)
            return null;
        using var json = JsonDocument.Parse(data);
        return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array
            ? changes.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString()! : c.GetRawText()).ToList()
            : null;
    }

    static ArtifactView ArtifactViewOf(ArtifactRecord a) =>
        new(a.StepId, a.Execution, a.Name, a.Type, a.Ref, a.Action, a.Evidence, ArtifactKindOf(a), a.At);

    /// <summary>What an indexed artifact is, from the names the engine gives what it indexes (<see cref="ArtifactNames"/>).</summary>
    static ArtifactKind ArtifactKindOf(ArtifactRecord a) => a switch
    {
        { StepId: "", Name: ArtifactNames.PlanRevision } => ArtifactKind.PlanRevision,
        _ when a.Name.StartsWith(ArtifactNames.TranscriptType + ":", StringComparison.Ordinal) => ArtifactKind.Transcript,
        _ when a.Name.StartsWith(ArtifactNames.PromptType + ":", StringComparison.Ordinal) => ArtifactKind.Prompt,
        { Evidence: true } => ArtifactKind.Evidence,
        _ => ArtifactKind.Artifact,
    };
}
