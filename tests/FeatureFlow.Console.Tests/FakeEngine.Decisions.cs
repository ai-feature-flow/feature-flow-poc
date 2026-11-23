using System.Text;
using System.Text.Json;
using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Console.Tests;

/// <summary>What <see cref="FakeEngine.DecisionsAsync"/> added to the scenario: findings to triage, a spec to approve with its documents, a session's workspace.</summary>
public sealed record DecisionScenario(ProjectScenario Tree, string Needing, string DecidedByPerson, string Ledger, ScenarioWait Spec, ScenarioWait Patch);

/// <summary>
/// The records console B1's decision popups show beyond the shared scenario (plan package e): a findings ledger whose
/// triage needs a person, a spec with requirements, criteria and their changes, and a session's workspace document; and
/// the <c>change_role</c> override, applied as the core applies it (a new plan revision).
/// </summary>
public sealed partial class FakeEngine
{
    partial void Decisions() => On(ChangeRoleAsync);

    async Task<string?> ChangeRoleAsync(Update update)
    {
        if (update is not { Name: RunWorkflowNames.Override, Argument: OverrideArgs { Key: "change_role" } o })
            return null;
        lock (Overrides)
            Overrides.Add(o);
        await TraceAsync(update.RunId, "override", o.StepId ?? "", $"change_role by {o.Actor}: {o.Reason} -> applied");
        return "applied: plan revision 2";
    }

    /// <summary>The project scenario with what the decision popups need in it.</summary>
    public async Task<DecisionScenario> DecisionsAsync(string prefix = "proj", string requester = "local")
    {
        var tree = await ProjectTreeAsync(prefix, requester);
        var ticket = tree.Triage.RunId;

        // A finding the triager declined though blocking: it needs the person. One a person declined earlier, restated by the
        // triager since: decided, not asked again. And a low one the triager declined: the rest of the ledger.
        await FindingAsync(ticket, "F-E9EA33EE28", "missing_test", "Compilation error in initial commit prevented tests from running", "high", true,
            ("triaged", "declined", "verify.fix.triage", "The compilation error was fixed in 20f1d7e; HEAD is correct."));
        var earlier = JsonSerializer.Serialize(new AnswerArgs("gate-0", requester, "triaged", Reason: "earlier triage"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await AppendAsync(new RecordBatch(Answers: [new AnswerRecord(ticket, "gate-0", requester, earlier, "recorded", Now(), "earlier triage")]));
        await FindingAsync(ticket, "F-E42DA136D6", "wrong_behaviour", "Tests do not test a newly added endpoint", "high", true,
            ("triaged", "escalated", "verify.fix.triage", "Needs a person"),
            ("triaged", "declined", requester, "Not a defect: the ticket asked for an existing endpoint"),
            ("triaged", "declined", "verify.fix.triage", "Restated"));
        await FindingAsync(ticket, "F-2F213E91CB", "missing_test", "No test for non-integer ids", "low", false,
            ("triaged", "declined", "verify.fix.triage", "Optional enhancement"));

        // A spec put to the person a second time: requirements and criteria, each with what changed.
        var spec = tree.SpecApproval.RunId;
        var requirements = await ProduceAsync(spec, "plan.plan", "requirements",
            "schema: 1\nscope: { kind: ticket, id: INV-1 }\nchanges:\n  - R2 split into R2 and R3\n" +
            "requirements:\n  - { id: R-001, title: List invoices, behaviour: GET /invoices lists the caller's invoices }\n" +
            "  - { id: R-002, title: Export invoices, behaviour: GET /invoices.csv exports them }\n",
            "std:artifact_types/requirements", execution: 2);
        var plan = await ProduceAsync(spec, "plan.plan", "delivery_plan",
            "schema: 1\nscope: { kind: ticket, id: INV-1 }\nchanges:\n  - AC-002 verified by a unit test\nacceptance_criteria:\n" +
            "  - id: AC-001\n    title: Invoices are listed\n    behaviour: GET returns 200 and the caller's invoices\n" +
            "    supports: [{ artifact: requirements, requirement: R-001 }]\n" +
            "    verification: [{ id: V1, kind: parsed_test, repository: acme/api, tier: unit, semantic_review: required, intent: Call the endpoint in-process }]\n",
            "std:artifact_types/delivery_plan", execution: 2);
        var specWait = await OpenWaitAsync(spec, "gate", "approve_spec", "ticket.spec_approval", "# Approve the plan for INV-1\n\nCheck each requirement.",
            consumed: [new ConsumedRef("requirements", "requirements", requirements), new ConsumedRef("delivery_plan", "delivery_plan", plan)]);

        await SessionWorkspaceAsync(tree.Session, "requirements.yaml", "schema: 1\nrequirements: []\n");

        // A patch that loads (the shared scenario's names a step without its routes): a step added before the children and a limit changed.
        var epic = tree.PatchApproval.RunId;
        var patch = await OpenPatchApprovalAsync(epic, "tickets.plan", new WaitPatch("agent", "tickets.plan", "INV-2 needs a migration first",
            ["it adds a step"], "tickets.children", 2, false,
            "api_version: feature-flow/v1\nkind: patch\nbase_revision: 2\nchanges:\n" +
            "  - op: insert\n    after: tickets.validate\n    step: { id: migrate, action: test.run, routes: { failed: plan, baseline_failed: plan } }\n" +
            "  - { op: set, target: tickets.plan, field: limits, value: { attempts: 4 } }\n"));
        return new DecisionScenario(tree, "F-E9EA33EE28", "F-E42DA136D6", "F-2F213E91CB", new(spec, specWait, "approve_spec"), new(epic, patch, "tickets.plan"));
    }

    /// <summary>A finding in the run's ledger, raised by review, then each event (kind, state, actor, reason) in order; its state the last event's.</summary>
    public async Task FindingAsync(string runId, string id, string rule, string title, string severity, bool blocking,
        params (string Kind, string State, string Actor, string Reason)[] events)
    {
        var finding = new ReportedFinding(rule, title, $"The reviewer's account of {id}.", severity, blocking, blocking ? "demonstrated" : "reasoned",
            new FindingLocation(ScenarioRepository, "tests/WidgetTests.cs", "GET_widgets"), "No action needed on HEAD.", "6eb07c1: broken\n20f1d7e: fixed");
        var state = events.Length == 0 ? "open" : events[^1].State;
        var history = new List<FindingEventRecord> { new(runId, id, "e00", "raised", "open", "verify.review.review", null, "verify.review", "6eb07c1", null, Now()) };
        history.AddRange(events.Select((e, i) => new FindingEventRecord(runId, id, $"e{i + 1:D2}", e.Kind, e.State, e.Actor, e.Reason, null, "6eb07c1", null, Now())));
        await AppendAsync(new RecordBatch(
            Findings: [new FindingRecord(runId, id, "fp-" + id, finding, "verify.review.review", null, state, "6eb07c1", "6eb07c1", 0, 0, 0, false, Now())],
            FindingEvents: history));
    }

    /// <summary>The workspace document a session opens with (as the engine stores it), with one draft seeded and one later step's checks offered.</summary>
    public async Task SessionWorkspaceAsync(ScenarioWait wait, string draft, string text)
    {
        var sessions = (await Records.QueryAsync<SessionRecord>(Queries.Sessions, new Dictionary<string, string> { ["run_id"] = wait.RunId }, Ct)).Unwrap();
        var session = sessions.Single(s => s.WaitId == wait.WaitId);
        var seed = (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), Ct)).Unwrap();
        var document = new SessionDocument(session.SessionId, wait.RunId, wait.WaitId, wait.StepId, session.Repository, session.Branch, session.BaseRevision, ".ff-session",
            [new SessionDocumentFile(draft, "requirements", "std:artifact_types/requirements", seed.Id)], "Write the project's requirements.",
            new Dictionary<string, string>(), ["check_requirements"], "submitted", "abandoned", "Fetch, edit, push, submit.");
        var stored = (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)))), Ct)).Unwrap();
        await AppendAsync(new RecordBatch(AgentEvents: [new AgentEventRecord(SessionNames.EventKey(session.SessionId), SessionNames.WorkspaceEvent, stored.Id, Now())]));
    }
}
