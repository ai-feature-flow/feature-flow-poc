using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Api.Contracts;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// The API's records (L3): each request record, as the CLI sends it, serialises with the API's options to the snake_case
/// keys the OpenAPI document names, and reads back as the API reads a body; every record and enum of
/// <c>FeatureFlow.Api.Contracts</c> is named in snake_case.
/// </summary>
public sealed class ContractRoundTripTests
{
    static readonly string Commit = new('a', 40);

    /// <summary>Every request record, with every property given.</summary>
    static readonly IReadOnlyDictionary<string, object> Requests = new object[]
    {
        new StartRunRequest("std:templates/ticket_delivery", ["acme/api"], new Dictionary<string, string> { ["ticket_id"] = "\"ACME-1\"" }, true, "ff/epic/x", "ACME-1", 500),
        new AnswerRequest("gate-1", "approved", "retry", "implement", true, 2, "failed", "the spec is complete",
            new Dictionary<string, string> { ["person_triage"] = "schema: 1\n" }),
        new AbortRequest("not needed", true),
        new OverrideRequest("skip", "flaky", "verify", new Dictionary<string, string> { ["outcome"] = "passed" }),
        new SessionCheckRequest("check_requirements", Commit),
        new SessionSubmitRequest(Commit, "ready"),
        new SessionAbandonRequest("not needed"),
        new RegisterRepositoryRequest("acme/api", "/srv/git/api"),
    }.ToDictionary(r => r.GetType().Name);

    [Theory]
    [InlineData(nameof(StartRunRequest))]
    [InlineData(nameof(AnswerRequest))]
    [InlineData(nameof(AbortRequest))]
    [InlineData(nameof(OverrideRequest))]
    [InlineData(nameof(SessionCheckRequest))]
    [InlineData(nameof(SessionSubmitRequest))]
    [InlineData(nameof(SessionAbandonRequest))]
    [InlineData(nameof(RegisterRepositoryRequest))]
    public async Task A_request_record_round_trips_with_the_keys_the_document_names(string name)
    {
        var (document, _) = await OpenApiTests.GeneratedAsync();
        var request = Requests[name];
        var type = request.GetType();

        var json = JsonSerializer.Serialize(request, type, ApiJson.Options);
        var keys = JsonNode.Parse(json)!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);
        var named = document["components"]!["schemas"]![type.Name]!["properties"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);
        Assert.Equal(named, keys);
        var read = JsonSerializer.Deserialize(json, type, ApiJson.Requests);
        Assert.Equal(json, JsonSerializer.Serialize(read, type, ApiJson.Options));
    }

    static readonly DateTimeOffset At = new(2026, 11, 25, 9, 30, 0, TimeSpan.Zero);

    static readonly FindingView Finding = new("F-0123456789", "declined", "missing_validation", "No limit on page size", "GET /invoices takes any page size.", "medium",
        false, "reasoned", new FindingLocationView("acme/api", "src/Invoices.cs", "List", "Take(size)", "AC-001"), "Cap the page size at 100", null, "run-1", true,
        "ada", new FindingRaisedView("verify.review.review_0.assess", "correctness", "agent", "claude-sonnet-5.5"), true, null, 1, 0, At);

    /// <summary>
    /// The response records of build B2's routes (<c>getStepDetail</c>, <c>getReviewReport</c>, <c>getRunPolicy</c>), with every
    /// property of their new records given and each list holding one entry.
    /// </summary>
    static readonly IReadOnlyDictionary<string, object> Responses = new object[]
    {
        new StepDetail("run-1", "plan.plan",
            [new StepExecutionView(1, At, At.AddMinutes(4), "planned", "plan.validate",
                [new ArtifactView("plan.plan", 1, "prompt:1", "prompt", "sha256:aa", null, false, ArtifactKind.Prompt, At)],
                [new AgentSessionView(1, "sha256:aa", "sha256:bb")],
                [new StepReportView(1, true, "planned", "Planned two criteria", ["none"], At)],
                [new StepQuestionView("q-1", "CSV only?", ["yes", "no"], "answered", "yes", At)])],
            [], [Finding], ["plan.plan"],
            [new StepEventView(At, StepEventSource.Agent, "patch_staged", 1, "staged against revision 1", "ada", "F-0123456789")],
            new StagedPatchView(1, "self", 1, "{\"changes\":[]}", At),
            [new InstructionView("o-1", "ada", "Keep the export to CSV", At)]),
        new ReviewReport("run-1", "Ticket INV-1", [new PullRequestLink("acme/api", 201, "https://github.com/acme/api/pull/201", "open")],
            [new ReviewPassView(1, "verify.review.review", At, At.AddMinutes(3),
                [new ReviewAngleView("correctness", "verify.review.review_0.assess", 1, "Sound.", ["src/Invoices.cs"], 1, 0, "sha256:cc")])],
            [new ReviewFindingView(Finding, 1, "out of scope", "ada", At)], [new ReviewFindingView(Finding with { LedgerId = "F-9876543210" }, null, null, null, null)],
            new TraceView("sha256:dd", false, [new TracedRequirementView("R-001", false, ["acme/api unit InvoicesTests.Lists at 0123456789"], ["R-001: no test is claimed"])]),
            "a note"),
        new RunPolicyView("run-1", [new DecisionPolicyView("ticket.merge_action", ["deliver.merge"], ["approver"], 1, false, "default")],
            [new OverridePolicyView("go_to", ["steerer", "admin"], true)], new AttemptPolicyView(3, 50, 10, 200)),
    }.ToDictionary(r => r.GetType().Name);

    /// <summary>
    /// A response record serialises with the API's options to the keys its schema in the document names, at every level
    /// (a nested record by its <c>$ref</c>), and reads back to the same JSON.
    /// </summary>
    [Theory]
    [InlineData(nameof(StepDetail))]
    [InlineData(nameof(ReviewReport))]
    [InlineData(nameof(RunPolicyView))]
    public async Task A_response_record_round_trips_with_the_keys_the_document_names(string name)
    {
        var (document, _) = await OpenApiTests.GeneratedAsync();
        var schemas = document["components"]!["schemas"]!.AsObject();
        var response = Responses[name];
        var type = response.GetType();

        var json = JsonSerializer.Serialize(response, type, ApiJson.Options);
        var problems = new List<string>();
        Walk(JsonNode.Parse(json)!, schemas[type.Name]!, type.Name);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        var read = JsonSerializer.Deserialize(json, type, ApiJson.Options);
        Assert.Equal(json, JsonSerializer.Serialize(read, type, ApiJson.Options));

        void Walk(JsonNode value, JsonNode schema, string where)
        {
            if (schema["$ref"]?.GetValue<string>() is { } reference)
                schema = schemas[reference.Split('/')[^1]]!;
            if (schema["oneOf"] is JsonArray choices)
                schema = choices.First(c => c?["type"]?.ToString() != "null")!;
            if (schema["$ref"] is not null)
            {
                Walk(value, schema, where);
                return;
            }

            if (value is JsonArray items)
            {
                foreach (var item in items.OfType<JsonNode>())
                    Walk(item, schema["items"]!, where + "[]");
                return;
            }

            if (value is not JsonObject obj || schema["properties"] is not JsonObject properties)
                return;
            var keys = obj.Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
            var named = properties.Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
            if (!keys.SequenceEqual(named))
                problems.Add($"{where}: written [{string.Join(", ", keys)}], the document names [{string.Join(", ", named)}]");
            foreach (var (key, inner) in obj)
                if (inner is not null && properties[key] is { } property)
                    Walk(inner, property, $"{where}.{key}");
        }
    }

    [Fact]
    public void Every_request_record_is_round_tripped() =>
        Assert.Equal(typeof(ApiJson).Assembly.GetExportedTypes().Where(t => t.Name.EndsWith("Request", StringComparison.Ordinal)).Select(t => t.Name).Order(StringComparer.Ordinal),
            Requests.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void Every_record_property_and_enum_value_is_named_in_snake_case()
    {
        var wrong = new List<string>();
        // GetTypeInfo needs the options locked, which a serialisation does; this test may run before any other.
        ApiJson.Options.MakeReadOnly(populateMissingResolver: true);
        foreach (var type in typeof(ApiJson).Assembly.GetExportedTypes().Where(t => t != typeof(ApiJson)))
        {
            if (type.IsEnum)
            {
                foreach (var value in Enum.GetValues(type))
                {
                    var written = JsonSerializer.Serialize(value, type, ApiJson.Options).Trim('"');
                    if (written != JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()!))
                        wrong.Add($"{type.Name}.{value} is written {written}");
                }

                continue;
            }

            var info = ApiJson.Options.GetTypeInfo(type);
            foreach (var property in info.Properties)
            {
                var clr = (property.AttributeProvider as MemberInfo)?.Name ?? property.Name;
                if (property.Name != JsonNamingPolicy.SnakeCaseLower.ConvertName(clr))
                    wrong.Add($"{type.Name}.{clr} is written {property.Name}");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
