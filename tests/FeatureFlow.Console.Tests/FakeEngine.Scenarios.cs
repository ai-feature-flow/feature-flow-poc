using System.Text;
using System.Text.Json;
using FeatureFlow.Interpreter;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Console.Tests;

/// <summary>An open wait of the scenario: the run it is in, its id and its step.</summary>
public sealed record ScenarioWait(string RunId, string WaitId, string StepId);

/// <summary>
/// What <see cref="FakeEngine.ProjectTreeAsync"/> wrote: the root project run, its epics and their tickets (each list in
/// plan order), and one open wait of every kind B1's console answers.
/// </summary>
public sealed record ProjectScenario(
    string Root,
    IReadOnlyList<string> Epics,
    IReadOnlyList<string> Tickets,
    ScenarioWait Session,
    ScenarioWait PatchApproval,
    ScenarioWait SpecApproval,
    ScenarioWait Merge,
    ScenarioWait Breakpoint,
    ScenarioWait Intervention,
    ScenarioWait Triage,
    ScenarioWait Question);

/// <summary>
/// A run tree for the console's B1 tests (plan package d1): a project on <c>std:templates/project_delivery</c>, two epics on
/// <c>epic_delivery</c> and two tickets under each on <c>ticket_delivery</c>, each run with its library lock (the root's),
/// its plan revisions (a parent's second revision adds its children's branches, as <c>plan.expand</c> does), step states,
/// a timeline, and between them an open wait of every kind. It is a fixture: the engine would not hold all these waits at
/// once (the project's session, for one, comes before its epics), but each wait is written as the engine writes it.
/// </summary>
public sealed partial class FakeEngine
{
    /// <summary>The repository every run of the scenario works in.</summary>
    public const string ScenarioRepository = "acme/api";

    /// <summary>Writes the scenario under ids starting <paramref name="prefix"/> (<c>proj</c>, <c>proj-epic-1</c>, <c>proj-ticket-1</c>, …).</summary>
    public async Task<ProjectScenario> ProjectTreeAsync(string prefix = "proj", string requester = "local")
    {
        string[] repositories = [ScenarioRepository];
        var root = prefix;
        string[] epics = [$"{prefix}-epic-1", $"{prefix}-epic-2"];
        string[] tickets = [$"{prefix}-ticket-1", $"{prefix}-ticket-2", $"{prefix}-ticket-3", $"{prefix}-ticket-4"];
        string[] epicNames = ["invoices", "payments"];
        string[] ticketNames = ["INV-1", "INV-2", "PAY-1", "PAY-2"];

        // The project: its lock, its plan with both epics expanded, and the steps before them done.
        await RunWithAsync(root, "std:templates/project_delivery", requester: requester, repositories: repositories, level: "project",
            trackerIssue: $"{ScenarioRepository}#100", trackerTitle: "Billing", trackerUrl: $"https://github.com/{ScenarioRepository}/issues/100");
        await AppendAsync(new RecordBatch(Locks: [new LockRecord(root, await LockAsync(), Now())]));
        await PlanAsync(root, "std:templates/project_delivery", repositories, new() { ["project_name"] = "billing" }, "epics.expand", epicNames);
        await DoneAsync(root, ("check_requirements", "valid"), ("approve", "approved"), ("epics.plan", "planned"), ("epics.validate", "valid"),
            ("epics.approve", "approved"), ("epics.expand", "expanded"));
        await TraceAsync(root, "started", "epics.children");

        for (var e = 0; e < epics.Length; e++)
        {
            await ChildRunAsync(root, $"epics.children_{e}", epics[e], "std:templates/epic_delivery", requester: requester, repositories: repositories,
                level: "epic", trackerIssue: epicNames[e]);
            await PlanAsync(epics[e], "std:templates/epic_delivery", repositories, new() { ["epic_name"] = epicNames[e] }, "tickets.expand",
                ticketNames.Skip(2 * e).Take(2).ToList());
            await DoneAsync(epics[e], ("baseline.tests", "passed"), ("design", "designed"), ("validate_design", "valid"), ("tickets.plan", "planned"),
                ("tickets.validate", "valid"), ("tickets.approve", "approved"), ("tickets.expand", "expanded"));
            await TraceAsync(epics[e], "started", "tickets.children");
            for (var t = 2 * e; t < 2 * e + 2; t++)
            {
                await ChildRunAsync(epics[e], $"tickets.children_{t - 2 * e}", tickets[t], requester: requester, repositories: repositories,
                    trackerIssue: ticketNames[t], trackerTitle: $"Ticket {ticketNames[t]}");
                await PlanAsync(tickets[t], "std:templates/ticket_delivery", repositories, new() { ["ticket_id"] = ticketNames[t] });
                await DoneAsync(tickets[t], ("baseline.tests", "passed"), ("baseline.analysis", "passed"), ("plan.plan", "planned"), ("plan.validate", "valid"));
                await StepStateAsync(tickets[t], "plan.plan", attemptsAllowed: 3);
            }
        }

        // The project's requirements session, prepared for the person.
        var session = await OpenWaitAsync(root, "gate", "define", "project.requirements_session", "Write the project's requirements in a session", ["submitted", "abandoned"]);
        await AppendAsync(new RecordBatch(Sessions:
            [new SessionRecord($"{root}-session-1", root, session, "define", requester, ScenarioRepository, $"ff/session/{root}/{session}", Commit, SessionStatuses.Prepared, null, null, Now())]));

        // Epic 1: the ticket planner proposed a patch to the epic's plan. Epic 2: paused at a breakpoint before verify.
        var patch = await OpenPatchApprovalAsync(epics[0], "tickets.plan", new WaitPatch("agent", "tickets.plan", "INV-2 needs a migration ticket first",
            ["it adds a step"], "tickets.children", 2, false,
            "api_version: feature-flow/v1\nkind: patch\nbase_revision: 2\nchanges:\n  - op: insert\n    after: tickets.validate\n    step: { id: migrate, action: test.run }\n"));
        var breakpoint = await OpenWaitAsync(epics[1], "breakpoint", "verify.tests", "epic.breakpoint", "a person set a breakpoint before verify.tests");
        await TraceAsync(epics[1], "paused", "verify.tests", breakpoint);

        // Ticket 1: its spec waits on approval, the requirements revised (LOG 508).
        var requirements = await ProduceAsync(tickets[0], "plan.plan", "requirements",
            "schema: 1\nscope: { kind: ticket, id: INV-1 }\nchanges:\n  - R2 split into R2 and R3: the export and the email are separate behaviours\n" +
            "requirements:\n  - { id: R1, title: List invoices, behaviour: GET /invoices lists the caller's invoices }\n",
            "std:artifact_types/requirements");
        var plan = await ProduceAsync(tickets[0], "plan.plan", "delivery_plan", "schema: 1\nscope: { kind: ticket, id: INV-1 }\n", "std:artifact_types/delivery_plan");
        var spec = await OpenWaitAsync(tickets[0], "gate", "approve_spec", "ticket.spec_approval", "Approve the plan for INV-1?",
            consumed: [new ConsumedRef("requirements", "requirements", requirements), new ConsumedRef("delivery_plan", "delivery_plan", plan)]);

        // Ticket 2: its pull request is ready and the merge waits on a person.
        await DoneAsync(tickets[1], ("approve_spec", "approved"), ("implement", "implemented"), ("verify.tests", "passed"), ("deliver.open_pr", "ready"),
            ("deliver.checks", "passed"));
        await PullRequestAsync(tickets[1], ScenarioRepository, 201, "deliver.merge", title: "List invoices (#201)");
        await TraceAsync(tickets[1], "started", "deliver.merge");
        var merge = await OpenWaitAsync(tickets[1], "gate", "deliver.merge", "ticket.merge_action",
            $"may {ScenarioRepository}#201 be merged into ff/epic/invoices? Approve to ask a person to merge it on the code host; reject to decline");

        // Ticket 3: the implementer used its attempts, sent back by review each time.
        await DoneAsync(tickets[2], ("approve_spec", "approved"));
        await StepStateAsync(tickets[2], "implement", executions: 3, attemptsUsed: 3, attemptsAllowed: 3);
        var intervention = await OpenInterventionAsync(tickets[2], "implement",
            ExhaustedCause("implement", 3, "the tests still fail", "verify.review", "missing null check", "missing null check", "missing null check"));

        // Ticket 4: the implementer asks a question, and a finding waits on a person's triage.
        await DoneAsync(tickets[3], ("approve_spec", "approved"));
        await TraceAsync(tickets[3], "started", "implement");
        var question = await OpenWaitAsync(tickets[3], "question", "implement", "", "Are XLSX exports in scope, or CSV only?", ["CSV only", "CSV and XLSX"]);
        var triage = await OpenWaitAsync(tickets[3], "gate", "verify.fix.person_triage", "findings.triage", "Triage the findings the agent may not clear",
            ["triaged", "escalate"]);

        return new ProjectScenario(root, epics, tickets,
            new(root, session, "define"), new(epics[0], patch, "tickets.plan"), new(tickets[0], spec, "approve_spec"), new(tickets[1], merge, "deliver.merge"),
            new(epics[1], breakpoint, "verify.tests"), new(tickets[2], intervention, "implement"), new(tickets[3], triage, "verify.fix.person_triage"),
            new(tickets[3], question, "implement"));
    }

    /// <summary>A commit id, for the session's base.</summary>
    const string Commit = "89abcdef0123456789abcdef0123456789abcdef";

    /// <summary>Traces each step completed with its outcome, in order.</summary>
    async Task DoneAsync(string runId, params (string StepId, string Outcome)[] steps)
    {
        foreach (var (stepId, outcome) in steps)
        {
            await TraceAsync(runId, "started", stepId);
            await TraceAsync(runId, "completed", stepId, outcome);
        }
    }

    /// <summary>The engine's libraries as a run's lock: every file stored by content, then the lock itself, as <c>POST /runs</c> writes it; returns its reference.</summary>
    async Task<string> LockAsync()
    {
        var locked = new List<LockedLibrary>();
        foreach (var library in Libraries.All)
        {
            foreach (var (_, text) in library.Files.All)
                (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), Ct)).Unwrap();
            locked.Add(LibraryLock.Pin(library));
        }

        var lockText = JsonSerializer.Serialize(new LibraryLock(locked), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(lockText)), Ct)).Unwrap().Id;
    }

    /// <summary>
    /// Indexes the run's plan revision 1 as the record activity does; with <paramref name="expandStep"/>, also revision 2, the
    /// patch that <c>plan.expand</c> builds from <paramref name="items"/> (each a work-list item's name) applied.
    /// </summary>
    async Task PlanAsync(string runId, string template, IReadOnlyList<string> repositories, Dictionary<string, string> parameters,
        string? expandStep = null, IReadOnlyList<string>? items = null)
    {
        var bound = parameters.ToDictionary(p => p.Key, p => YamlWriter.Read(JsonSerializer.Serialize(p.Value)));
        var (core, diagnostics) = RunCore.Start(Libraries, LibraryReference.TryParse(template)!, new RunInfo(runId, repositories, bound));
        if (core is null)
            throw new InvalidOperationException($"{template} does not start: {string.Join("; ", diagnostics.Select(d => d.Message))}");
        var revisions = core.TakeRevisions().ToList();
        if (expandStep is not null)
        {
            var nodes = items!.Select(name => YamlWriter.Read($"{{ name: {name}, repositories: [{string.Join(", ", repositories)}] }}")).ToList();
            var (patch, error) = PlanExpansion.Build(core, expandStep, new Dictionary<string, object?> { ["shape"] = "parallel", ["target"] = "children" }, nodes);
            if (patch is null)
                throw new InvalidOperationException($"{expandStep} does not expand: {error}");
            var expanded = revisions[^1] with { Revision = revisions[^1].Revision + 1, Patches = [.. revisions[^1].Patches, PatchReader.Write(patch)] };
            var d = new DiagnosticBag();
            if (expanded.Load(Libraries, d) is null)
                throw new InvalidOperationException($"{runId}'s expanded plan does not load: {string.Join("; ", d.Select(x => x.Message))}");
            revisions.Add(expanded);
        }

        foreach (var revision in revisions)
        {
            var stored = (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(revision.Write())), Ct)).Unwrap();
            await AppendAsync(new RecordBatch(Artifacts:
                [new ArtifactRecord(runId, "", revision.Revision, ArtifactNames.PlanRevision, ArtifactNames.PlanRevision, stored.Id, null, false, Now())]));
        }
    }
}
