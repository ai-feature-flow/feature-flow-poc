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
    /// route, which the console reads, is mapped here too.
    /// </summary>
    static void MapConsole(WebApplication app, Handlers api)
    {
        app.MapGet("/console", () => ConsoleFile("index.html", "no-cache")).ExcludeFromDescription();
        app.MapGet("/console/assets/{**file}", (string file) => ConsoleFile($"assets/{file}", "public, max-age=31536000, immutable"))
            .ExcludeFromDescription();
        app.MapGet("/runs/{id}/plan", (HttpContext c, string id) => api.Guard(c, caller => api.PlanAsync(c, caller, id)))
            .Described<PlanView>("getRunPlan", 200, 404, 409);
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
        /// from the run's timeline and open waits, whether a breakpoint is set on it, and the outcome it is skipped with.
        /// </summary>
        public async Task<IResult> PlanAsync(HttpContext context, Caller caller, string id)
        {
            var ct = context.RequestAborted;
            var p = new Dictionary<string, string> { ["run_id"] = id };
            if (await VisibleRunAsync(caller, id, ct) is not { } run)
                return Error(404, $"no run {id}");
            var latest = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, p, ct)).Unwrap()
                .Where(a => a.RunId == id && a is { StepId: "", Name: ArtifactNames.PlanRevision }).MaxBy(a => a.Execution);
            if (latest is null)
                return Error(404, $"run {id} has recorded no plan revision yet");
            if (await LockOfAsync(run, ct) is not { } lockRef)
                return Error(404, $"run {id} has no library lock to show its plan from");
            var d = new DiagnosticBag();
            var lockDocument = JsonSerializer.Deserialize<LibraryLock>(await ReadTextAsync(lockRef, ct), Stored)!;
            var libraries = new List<Library.Library>();
            foreach (var locked in lockDocument.Libraries)
            {
                var files = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (path, hash) in locked.Files)
                    files[path] = await ReadTextAsync(hash, ct);
                if (Library.Library.FromFiles(new LibraryFiles(files), d) is { } library)
                    libraries.Add(library);
            }

            var source = PlanSource.Read(await ReadTextAsync(latest.Ref, ct));
            if (source.Load(new LibrarySet(libraries), d) is not { } plan)
                return Error(409, $"revision {source.Revision} of run {id} does not load: {string.Join("; ", d.Where(x => x.Blocks).Take(3).Select(x => x.Message))}");
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

        static StepForm FormOf(Step step) => ReadableForm.Form(step) switch
        {
            "agent" => StepForm.Agent,
            "decision" => StepForm.Decision,
            "action" => StepForm.Action,
            "chain" => StepForm.Chain,
            "for_each" => StepForm.ForEach,
            _ => StepForm.Parallel,
        };

        /// <summary>
        /// The breakpoints set on steps, and the outcome each step is skipped with, from the run's timeline: what the core
        /// accepted, not what was asked (a refused override changes nothing).
        /// </summary>
        static (HashSet<string> Breakpoints, Dictionary<string, string> Skips) Steering(IEnumerable<TimelineRecord> timeline)
        {
            var breakpoints = new HashSet<string>(StringComparer.Ordinal);
            var skips = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in timeline.Where(t => t.StepId.Length > 0))
                switch (t.Kind)
                {
                    case "breakpoint_set":
                        breakpoints.Add(t.StepId);
                        break;
                    case "breakpoint_cleared":
                        breakpoints.Remove(t.StepId);
                        break;
                    case "skip_set" or "skipped" when t.Detail is { Length: > 0 } detail:
                        skips[t.StepId] = detail.Split(' ')[0];
                        break;
                }

            return (breakpoints, skips);
        }

        /// <summary>The lock of the run's tree: children run on their root's lock.</summary>
        async Task<string?> LockOfAsync(RunRecord run, CancellationToken ct)
        {
            for (var (current, depth) = (run, 0); current is not null && depth < 32; depth++)
            {
                if ((await Records.QueryAsync<LockRecord>(Queries.Lock, new Dictionary<string, string> { ["run_id"] = current.RunId }, ct)).Unwrap().FirstOrDefault() is { } found)
                    return found.LockRef;
                current = current.ParentRunId is { } parent
                    ? (await Records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = parent }, ct)).Unwrap().FirstOrDefault()
                    : null;
            }

            return null;
        }

        async Task<string> ReadTextAsync(string reference, CancellationToken ct)
        {
            await using var stream = (await Artifacts.GetAsync(new ArtifactRef(reference), ct)).Unwrap();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }

        /// <summary>Each step's latest status from the timeline, an open wait overriding it.</summary>
        static Dictionary<string, string> Statuses(IEnumerable<TimelineRecord> timeline, IEnumerable<WaitRecord> waits)
        {
            var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var t in timeline.Where(t => t.StepId.Length > 0))
                if (t.Kind switch
                    {
                        "started" or "resumed" => "running",
                        "completed" => $"done({t.Detail})",
                        "auto_resolved" => "done(auto)",
                        "retried" or "attempt_failed" => "retrying",
                        "interrupted" or "escalated" or "cancelled" or "exhausted" => t.Kind,
                        _ => null,
                    } is { } status)
                    statuses[t.StepId] = status;
            foreach (var w in waits.Where(w => w.StepId.Length > 0))
                statuses[w.StepId] = $"waiting({w.Kind})";
            return statuses;
        }
    }
}
