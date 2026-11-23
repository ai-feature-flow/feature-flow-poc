using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Step = FeatureFlow.Model.Plans.Step;
using StepForm = FeatureFlow.Api.Contracts.StepForm;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>
    /// The console (docs/12 section 4, A4): the single-page application built from src/FeatureFlow.Console and embedded as
    /// <c>console/&lt;path&gt;</c> (LOG 453). It holds no data and reads everything through the API with the person's own
    /// credential, so authentication and authorisation are the API's (LOG 229). Its index is always revalidated, so a deploy
    /// is picked up at once; its assets are named by their hash, so they are kept for a year (LOG 454). The run's plan
    /// route, which the console reads, is mapped here too; <c>?proposed=</c> is answered 501 until B1's A8 builds it.
    /// </summary>
    static void MapConsole(WebApplication app, Handlers api)
    {
        app.MapGet("/console", () => ConsoleFile("index.html", "no-cache")).ExcludeFromDescription();
        app.MapGet("/console/assets/{**file}", (string file) => ConsoleFile($"assets/{file}", "public, max-age=31536000, immutable"))
            .ExcludeFromDescription();
        app.MapGet("/runs/{id}/plan", (HttpContext c, string id, string? proposed) => api.Guard(c, caller => api.PlanAsync(c, caller, id, proposed)))
            .Described<PlanView>("getRunPlan", 200, 404, 409).Produces(StatusCodes.Status204NoContent);
    }

    /// <summary>
    /// The console's files by path under /console/, read once from the assembly. MSBuild names a resource in a folder with the
    /// build machine's separator, so a name is read with either.
    /// </summary>
    static readonly Lazy<Dictionary<string, byte[]>> ConsoleFiles = new(() =>
    {
        var assembly = typeof(ApiApp).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("console/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            files[name["console/".Length..].Replace('\\', '/')] = bytes.ToArray();
        }

        return files;
    });

    static IResult ConsoleFile(string path, string cacheControl)
    {
        if (ConsoleFiles.Value.Count == 0)
            return Results.Json(new ErrorResponse("this build has no console"), Json, statusCode: StatusCodes.Status404NotFound);
        return ConsoleFiles.Value.TryGetValue(path, out var content)
            ? new ConsoleResult(content, ContentTypeOf(path), cacheControl)
            : Results.Json(new ErrorResponse($"the console has no file {path}"), Json, statusCode: StatusCodes.Status404NotFound);
    }

    static string ContentTypeOf(string path) => Path.GetExtension(path) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// A console file, with headers that keep it to its own origin: no framing, no inline script or style, nothing loaded
    /// from elsewhere, and Trusted Types, so a string assigned to a markup sink such as <c>innerHTML</c> throws (LOG 455).
    /// </summary>
    sealed class ConsoleResult(byte[] content, string contentType, string cacheControl) : IResult
    {
        public const string Policy = "default-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'; " +
                                     "require-trusted-types-for 'script'; trusted-types 'none'";

        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.ContentType = contentType;
            context.Response.ContentLength = content.Length;
            context.Response.Headers.ContentSecurityPolicy = Policy;
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = cacheControl;
            await context.Response.Body.WriteAsync(content, context.RequestAborted);
        }
    }

    sealed partial class Handlers
    {
        /// <summary>
        /// A run's current plan revision (G11; LOG 404): the latest revision the run indexed, rebuilt with the libraries
        /// locked when its root run started, as its readable form (docs/09 section 4.4) and as data, each step with its status
        /// from the run's timeline and open waits, whether a breakpoint is set on it, and the outcome it is skipped with. A run
        /// that has indexed no revision yet answers 204: it has no plan to show, which is not a refusal (LOG 493).
        /// </summary>
        public async Task<IResult> PlanAsync(HttpContext context, Caller caller, string id, string? proposed)
        {
            var ct = context.RequestAborted;
            var p = new Dictionary<string, string> { ["run_id"] = id };
            if (await VisibleRunAsync(caller, id, ct) is not { } run)
                return Error(404, $"no run {id}");
            if (proposed is not null)
                return Error(501, "not built yet (B1 A8)");
            var (loaded, refusal) = await LoadPlanAsync(run, revision: null, extraPatch: null, ct);
            if (refusal is { Status: 204 })
                return Results.NoContent();
            if (refusal is not null)
                return Error(refusal.Status, refusal.Message);
            var (plan, source, _) = loaded!;
            var timeline = (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string>(p) { ["after"] = "0" }, ct)).Unwrap();
            var waits = (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string>(p) { ["open"] = "true" }, ct)).Unwrap();
            var statuses = Statuses(timeline, waits);
            var (breakpoints, skips) = Steering(timeline);
            var steps = plan.AllSteps.Select(s => new PlanStepView(s.Id, s.Scope.ContainerId, FormOf(s), ReadableForm.RoleOrAction(s),
                s.Outcomes().Select(o => new PlanOutcomeView(o.Name, o.Class.Name(),
                    s.ResolvedRoutes.FirstOrDefault(r => r.Outcome == o.Name) is { } route ? ReadableForm.Show(route.Target) : null)).ToList(),
                s.Protected, statuses.GetValueOrDefault(s.Id) ?? "pending", breakpoints.Contains(s.Id), skips.GetValueOrDefault(s.Id))).ToList();
            return Results.Json(new PlanView(id, source.Revision, source.Template, ReadableForm.Render(plan, statuses), steps), Json);
        }
    }
}
