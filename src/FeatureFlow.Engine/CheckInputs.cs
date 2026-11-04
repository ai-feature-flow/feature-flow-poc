using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine;

/// <summary>
/// Pre-submit parity (docs/09 section 4.2): a digest of everything a step's activity is given apart from who it is (its
/// identity): the action or role, the rendered inputs (an action's task), the contract, the agent's settings (task, tools,
/// write access; its model follows from its role, docs/07 section 3, <c>role_mapping</c>) and an engine action's scope,
/// whose consumed artifacts are content references and so pin the commit the session's files were imported at. A session's
/// check and the later step's own run record it; equal digests mean the check was given what the step was.
/// </summary>
public static class CheckInputs
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Digest(StepCall call, ActionScope? scope)
    {
        var given = new JsonObject
        {
            ["form"] = call.Form,
            ["what"] = call.What,
            ["inputs"] = JsonSerializer.SerializeToNode(call.Inputs, Json),
            ["contract"] = JsonSerializer.SerializeToNode(call.Contract, Json),
            ["dry_run"] = call.DryRun,
            ["timeout"] = call.Timeout?.ToString(),
            ["agent"] = JsonSerializer.SerializeToNode(call.Agent, Json),
            ["scope"] = JsonSerializer.SerializeToNode(scope, Json),
        };
        var text = Canonical(given)!.ToJsonString();
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The same value with every object's keys in ordinal order, so a map's insertion order does not change the digest.</summary>
    static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Canonical(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
        _ => node?.DeepClone(),
    };
}
