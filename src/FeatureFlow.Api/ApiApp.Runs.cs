using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Temporalio.Client;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>Runs: start, list and show.</summary>
    static void MapRuns(WebApplication app, Handlers api)
    {
        app.MapPost("/runs", (Delegate)((HttpContext c) => api.Guard(c, caller => api.StartAsync(c, caller))));
        app.MapGet("/runs", (Delegate)((HttpContext c) => api.Guard(c, caller => api.ListAsync(c, caller))));
        app.MapGet("/runs/{id}", (HttpContext c, string id) => api.Guard(c, caller => api.ShowAsync(c, caller, id)));
        app.MapGet("/runs/{id}/artifacts/{reference}", (HttpContext c, string id, string reference) => api.Guard(c, caller => api.ArtifactAsync(c, caller, id, reference)));
    }

    sealed partial class Handlers
    {
        /// <summary>A plain branch name: not a full reference, no spaces or Git's forbidden characters, no empty or dot-led parts.</summary>
        static bool ValidBranch(string name) =>
            name.Length is > 0 and <= 200 && !name.StartsWith("refs/", StringComparison.Ordinal) && !name.EndsWith('/') && !name.EndsWith(".lock", StringComparison.Ordinal)
            && !name.Contains("..", StringComparison.Ordinal) && !name.Contains("@{", StringComparison.Ordinal)
            && name.All(c => c > ' ' && c != 127 && c is not ('~' or '^' or ':' or '?' or '*' or '[' or '\\'))
            && name.Split('/').All(part => part.Length > 0 && !part.StartsWith('.'));

        public async Task<IResult> StartAsync(HttpContext context, Caller caller)
        {
            var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
            var template = body?["template"]?.GetValue<string>();
            if (template is null || LibraryReference.TryParse(template) is null)
                return Error(400, "template must be a template reference, such as std:templates/ticket_delivery");
            var repositories = body!["repositories"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
            // Starting a run steers work in its repositories (LOG 219); a run is in at most one team's scope (LOG 217).
            if (Approval.MoreThanOneTeam(services.Organisation, repositories) is { } teams)
                return Error(400, teams);
            var scope = ScopeOf(repositories);
            if (Refuse(caller, scope, [PeopleRoles.Steerer], "start a run") is { } refused)
                return refused;
            var parameters = (body["parameters"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => kv.Value?.ToJsonString() ?? "null");
            var dryRun = body["dry_run"]?.GetValue<bool>() ?? false;
            // The branch the run's work branch starts from and merges into (LOG 137); default the repository's default branch.
            var parentBranch = body["parent_branch"]?.GetValue<string>();
            if (parentBranch is not null && !ValidBranch(parentBranch))
                return Error(400, $"parent_branch must be a branch name, such as ff/epic/billing, not '{parentBranch}'");
            // The run's tracker issue (run.tracker_issue): its input, read from the tracker, and where declarative updates go.
            var trackerIssue = body["tracker_issue"]?.GetValue<string>();
            if (trackerIssue is not null && (trackerIssue.Length is 0 or > 200 || trackerIssue.Any(char.IsControl)))
                return Error(400, "tracker_issue must be a tracker key, such as ACME-12 or owner/repo#12");
            var handOver = services.Installation.ContinueAsNewEvents;
            if (body["continue_as_new_events"]?.GetValue<long>() is { } requested)
            {
                // A run's own hand-over threshold is an administrator's override (LOG 87), and never below the floor.
                if (Refuse(caller, scope, [PeopleRoles.Admin], "set continue_as_new_events") is { } notAdmin)
                    return notAdmin;
                if (requested < ConfigurationReader.MinimumHandOverEvents)
                    return Error(400, $"continue_as_new_events must be at least {ConfigurationReader.MinimumHandOverEvents}");
                handOver = requested;
            }

            var lockRef = await WriteLockAsync(context.RequestAborted);
            var runId = "run-" + Guid.NewGuid().ToString("N")[..12];
            var now = DateTimeOffset.UtcNow;
            await Records.AppendAsync(new RecordBatch(
                [new RunRecord(runId, null, template, "starting", caller.Actor, repositories, dryRun, now, null)],
                Locks: [new LockRecord(runId, lockRef, now)]), context.RequestAborted);
            var start = new RunStart(runId, template, repositories, parameters, lockRef, dryRun, caller.Actor, HandOverEvents: handOver, ParentBranch: parentBranch,
                TrackerIssue: trackerIssue);
            await services.Client.StartWorkflowAsync(RunWorkflowNames.Workflow, [start],
                new WorkflowOptions(runId, services.Installation.TaskQueue));
            return Results.Json(new { run_id = runId }, Json, statusCode: 201);
        }

        /// <summary>
        /// The run's lock (docs/08 sections 3 and 5.1): every content file of the organisation's libraries stored
        /// by content hash, and the lock that lists them stored as an artifact itself.
        /// </summary>
        async Task<string> WriteLockAsync(CancellationToken ct)
        {
            var locked = new List<LockedLibrary>();
            foreach (var source in services.Organisation.Libraries)
            {
                var (library, problem) = await LibrarySources.OpenAsync(source, services.Organisation.Location, ct);
                if (library is null)
                    throw new InvalidOperationException($"library {source.Name}: {problem}");
                foreach (var (_, text) in library.Files.All)
                    (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), ct)).Unwrap();
                locked.Add(LibraryLock.Pin(library));
            }

            var lockText = JsonSerializer.Serialize(new LibraryLock(locked), Json);
            return (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(lockText)), ct)).Unwrap().Id;
        }

        /// <summary>The runs the caller may read: those where it is a viewer in the run's scope (docs/05 section 3.3).</summary>
        public async Task<IResult> ListAsync(HttpContext context, Caller caller)
        {
            var runs = (await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string>(), context.RequestAborted)).Unwrap()
                .Where(r => RolesIn(caller, ScopeOf(r.Repositories)).Contains(PeopleRoles.Viewer)).ToList();
            return Results.Json(new { runs }, Json);
        }

        public async Task<IResult> ShowAsync(HttpContext context, Caller caller, string id)
        {
            var p = new Dictionary<string, string> { ["run_id"] = id };
            var run = (await Records.QueryAsync<RunRecord>(Queries.Run, p, context.RequestAborted)).Unwrap().FirstOrDefault();
            if (run is null || !RolesIn(caller, ScopeOf(run.Repositories)).Contains(PeopleRoles.Viewer))
                return Error(404, $"no run {id}");
            var waits = (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string>(p) { ["open"] = "true" }, context.RequestAborted)).Unwrap();
            var after = context.Request.Query["after"].FirstOrDefault() ?? "0";
            var timeline = (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string>(p) { ["after"] = after }, context.RequestAborted)).Unwrap();
            var children = (await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string> { ["parent_run_id"] = id }, context.RequestAborted)).Unwrap();
            return Results.Json(new { run, waits, timeline, children }, Json);
        }

        /// <summary>
        /// An artifact the run itself recorded (the references its timeline shows), for a viewer of the run: what a person reads
        /// before ratifying it (docs/01). A child's artifacts are read through the child, whose scope may differ (review R5, F1).
        /// </summary>
        public async Task<IResult> ArtifactAsync(HttpContext context, Caller caller, string id, string reference)
        {
            var p = new Dictionary<string, string> { ["run_id"] = id };
            var run = (await Records.QueryAsync<RunRecord>(Queries.Run, p, context.RequestAborted)).Unwrap().FirstOrDefault();
            if (run is null || !RolesIn(caller, ScopeOf(run.Repositories)).Contains(PeopleRoles.Viewer))
                return Error(404, $"no run {id}");
            var indexed = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, p, context.RequestAborted)).Unwrap();
            if (!indexed.Any(a => a.RunId == id && a.Ref == reference))
                return Error(404, $"no artifact {reference} in run {id}");
            var read = await Artifacts.GetAsync(new ArtifactRef(reference), context.RequestAborted);
            return read.Ok ? Results.Stream(read.Value!, "application/octet-stream") : Error(404, $"artifact {reference} cannot be read: {read.Error!.Message}");
        }
    }
}
