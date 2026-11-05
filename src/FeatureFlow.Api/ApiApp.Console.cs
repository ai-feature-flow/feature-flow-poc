using System.Reflection;
using System.Text;
using System.Text.Json;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>
    /// The minimal web console (docs/11 section 4, M7; open question 5): runs, a run's readable form and timeline, and
    /// decisions to answer. One static page and its script, with no build chain; it holds no data and reads everything
    /// through the API with the person's own credential, and answers through the same answer call, so authentication and
    /// authorisation are the API's (LOG 229). The readable form is served here too, as text.
    /// </summary>
    static void MapConsole(WebApplication app, Handlers api)
    {
        app.MapGet("/console", () => Page("console.html", "text/html; charset=utf-8"));
        app.MapGet("/console/console.js", () => Page("console.js", "text/javascript; charset=utf-8"));
        app.MapGet("/runs/{id}/plan", (HttpContext c, string id) => api.Guard(c, caller => api.ReadableFormAsync(c, caller, id)));
    }

    static readonly Dictionary<string, string> Pages = new(StringComparer.Ordinal);

    static IResult Page(string name, string contentType)
    {
        string text;
        lock (Pages)
            if (!Pages.TryGetValue(name, out text!))
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FeatureFlow.Api.Console.{name}")
                                   ?? throw new InvalidOperationException($"the console's {name} is not embedded");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                Pages[name] = text = reader.ReadToEnd();
            }

        return new PageResult(text, contentType);
    }

    /// <summary>A console file, with headers that keep it to its own origin: no framing, scripts only from the API itself.</summary>
    sealed class PageResult(string text, string contentType) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.ContentType = contentType;
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(text, context.RequestAborted);
        }
    }

    sealed partial class Handlers
    {
        /// <summary>
        /// A run's plan in its readable form (docs/09 section 4.4), from the libraries locked when its root run started, with
        /// each step's status from the run's timeline and open waits. It is the plan as the run started: changes made while
        /// it runs (expansions, accepted patches) are in its timeline.
        /// </summary>
        public async Task<IResult> ReadableFormAsync(HttpContext context, Caller caller, string id)
        {
            var ct = context.RequestAborted;
            var p = new Dictionary<string, string> { ["run_id"] = id };
            if (await VisibleRunAsync(caller, id, ct) is not { } run)
                return Error(404, $"no run {id}");
            if (await LockOfAsync(run, ct) is not { } lockRef || LibraryReference.TryParse(run.Template) is not { } template)
                return Error(404, $"run {id} has no library lock to show its plan from");
            var d = new DiagnosticBag();
            var lockDocument = JsonSerializer.Deserialize<LibraryLock>(await ReadTextAsync(lockRef, ct), Json)!;
            var libraries = new List<Library.Library>();
            foreach (var locked in lockDocument.Libraries)
            {
                var files = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (path, hash) in locked.Files)
                    files[path] = await ReadTextAsync(hash, ct);
                if (Library.Library.FromFiles(new LibraryFiles(files), d) is { } library)
                    libraries.Add(library);
            }

            if (PlanLoader.LoadTemplate(new LibrarySet(libraries), template, d) is not { } plan)
                return Error(409, $"the plan of run {id} does not load: {string.Join("; ", d.Where(x => x.Blocks).Take(3).Select(x => x.Message))}");
            var timeline = (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string>(p) { ["after"] = "0" }, ct)).Unwrap();
            var waits = (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string>(p) { ["open"] = "true" }, ct)).Unwrap();
            return Results.Text(ReadableForm.Render(plan, Statuses(timeline, waits)), "text/plain; charset=utf-8");
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
