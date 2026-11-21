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
    }.ToDictionary(r => r.GetType().Name);

    [Theory]
    [InlineData(nameof(StartRunRequest))]
    [InlineData(nameof(AnswerRequest))]
    [InlineData(nameof(AbortRequest))]
    [InlineData(nameof(OverrideRequest))]
    [InlineData(nameof(SessionCheckRequest))]
    [InlineData(nameof(SessionSubmitRequest))]
    [InlineData(nameof(SessionAbandonRequest))]
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

    [Fact]
    public void Every_request_record_is_round_tripped() =>
        Assert.Equal(typeof(ApiJson).Assembly.GetExportedTypes().Where(t => t.Name.EndsWith("Request", StringComparison.Ordinal)).Select(t => t.Name).Order(StringComparer.Ordinal),
            Requests.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void Every_record_property_and_enum_value_is_named_in_snake_case()
    {
        var wrong = new List<string>();
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
