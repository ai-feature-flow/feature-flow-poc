using FeatureFlow.Api.Contracts;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>A run's artifacts: its index, an artifact's bytes, and an artifact read as a document.</summary>
    static void MapArtifacts(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/artifacts", (HttpContext c, string id) => api.Guard(c, caller => api.ListArtifactsAsync(c, caller, id)))
            .Described<ArtifactList>("listArtifacts", 200, 404);
        app.MapGet("/runs/{id}/artifacts/{reference}", (HttpContext c, string id, string reference) => api.Guard(c, caller => api.ArtifactAsync(c, caller, id, reference)))
            .WithName("getArtifact").Produces<Stream>(200, "application/octet-stream").Produces<ErrorResponse>(401).Produces<ErrorResponse>(404);
        app.MapGet("/runs/{id}/artifacts/{reference}/document",
                (HttpContext c, string id, string reference) => api.Guard(c, caller => api.ArtifactDocumentAsync(c, caller, id, reference)))
            .Described<ArtifactDocument>("getArtifactDocument", 200, 404, 415);
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
            var p = new Dictionary<string, string> { ["run_id"] = id };
            if (await VisibleRunAsync(caller, id, ct) is null)
                return Error(404, $"no run {id}");
            var indexed = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, p, ct)).Unwrap();
            if (!indexed.Any(a => a.RunId == id && a.Ref == reference)
                && !(await Records.QueryAsync<WaitRecord>(Queries.Waits, p, ct)).Unwrap().Any(w => w.Consumed?.Any(c => c.Ref == reference) == true))
                return Error(404, $"no artifact {reference} in run {id}");
            var read = await Artifacts.GetAsync(new ArtifactRef(reference), ct);
            if (!read.Ok)
                return Error(404, $"artifact {reference} cannot be read: {read.Error!.Message}");
            // Saved, never shown: a browser that navigates to an artifact neither renders nor sniffs it (review R1).
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentDisposition = "attachment";
            return Results.Stream(read.Value!, "application/octet-stream");
        }

        /// <summary>An artifact of the run read as a document (A7): not built yet.</summary>
        public async Task<IResult> ArtifactDocumentAsync(HttpContext context, Caller caller, string id, string reference)
        {
            if (await VisibleRunAsync(caller, id, context.RequestAborted) is null)
                return Error(404, $"no run {id}");
            return Error(501, $"not built yet (B1 A7): no document of {reference}");
        }
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
