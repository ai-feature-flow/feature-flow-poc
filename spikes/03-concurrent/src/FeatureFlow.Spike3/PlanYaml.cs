using FeatureFlow.Spike.Contracts;
using YamlDotNet.RepresentationModel;

namespace FeatureFlow.Spike3;

/// <summary>
/// A real YAML reader for Spike 3's plans. Spike 2's line parser cannot read
/// guarded routes, inline route maps or nested inputs (run.child parameters,
/// parallel branches), all of which docs/17 4.1 and 4.2 use.
/// </summary>
public static class PlanYaml
{
    /// <summary>Generic steps get a long start-to-close: Spike 1 defaulted it to 125 s.</summary>
    private const string DefaultStartToCloseSeconds = "1800";

    public static Plan Load(string path, IDictionary<string, string>? parameters = null)
    {
        var stream = new YamlStream();
        using (var reader = new StreamReader(path))
            stream.Load(reader);
        var root = (YamlMappingNode)stream.Documents[0].RootNode;

        var plan = new Plan();
        if (root.Children.TryGetValue(new YamlScalarNode("metadata"), out var meta) && meta is YamlMappingNode m)
        {
            if (Scalar(m, "revision") is { } rev)
                plan.Revision = int.Parse(rev);
            if (Scalar(m, "scope") is { } scope)
                plan.Parameters["scope"] = scope;
            if (Scalar(m, "name") is { } name)
                plan.Parameters["plan_name"] = name;
        }

        var steps = (YamlSequenceNode)root.Children[new YamlScalarNode("steps")];
        foreach (YamlMappingNode node in steps)
        {
            var step = new Step
            {
                Id = Scalar(node, "id")!,
                Kind = Scalar(node, "kind")!,
                Protected = Scalar(node, "protected") == "true",
                SimulatedDuration = DefaultStartToCloseSeconds,
            };

            if (Child(node, "inputs") is YamlMappingNode inputs)
            {
                foreach (var (k, v) in inputs.Children)
                    step.Inputs[((YamlScalarNode)k).Value!] = ToObject(v);
            }

            if (Child(node, "gate") is YamlMappingNode gate)
                step.Gate = new StepGate { DecisionType = Scalar(gate, "decision_type") ?? "" };

            if (Child(node, "limits") is YamlMappingNode limits)
            {
                step.Limits = new StepLimits();
                if (Scalar(limits, "attempts") is { } a)
                    step.Limits.Attempts = int.Parse(a);
                step.Limits.StartToClose = Scalar(limits, "start_to_close");
                step.Limits.Heartbeat = Scalar(limits, "heartbeat");
            }

            if (Child(node, "requires") is YamlMappingNode requires &&
                Child(requires, "capabilities") is YamlSequenceNode caps)
            {
                step.Requires = new StepRequires
                {
                    Capabilities = caps.Children.Select(c => ((YamlScalarNode)c).Value!).ToList(),
                };
            }

            if (Child(node, "routes") is YamlMappingNode routes)
            {
                foreach (var (k, v) in routes.Children)
                    step.Routes[((YamlScalarNode)k).Value!] = Route(v);
            }

            plan.Steps.Add(step);
        }

        plan.Entry = plan.Steps[0].Id;
        if (parameters is not null)
        {
            foreach (var (k, v) in parameters)
                plan.Parameters[k] = v;
        }
        return plan;
    }

    private static RouteTarget Route(YamlNode node) => node switch
    {
        YamlScalarNode s => RouteTarget.ToStep(s.Value!),
        YamlMappingNode map when Scalar(map, "end") is { } end => RouteTarget.ToEnd(end),
        YamlMappingNode map when Scalar(map, "resume") is not null => RouteTarget.ToResume(),
        YamlSequenceNode seq => new RouteTarget
        {
            Guarded = seq.Children.Cast<YamlMappingNode>().Select(g => new GuardedRoute
            {
                When = Scalar(g, "when"),
                To = Route(Child(g, "to")!),
            }).ToList(),
        },
        _ => throw new InvalidDataException($"unreadable route at {node.Start}"),
    };

    private static object? ToObject(YamlNode node) => node switch
    {
        YamlScalarNode s => s.Value,
        YamlSequenceNode seq => seq.Children.Select(ToObject).ToList(),
        YamlMappingNode map => map.Children.ToDictionary(
            kv => ((YamlScalarNode)kv.Key).Value!, kv => ToObject(kv.Value), StringComparer.Ordinal),
        _ => null,
    };

    private static YamlNode? Child(YamlMappingNode node, string name) =>
        node.Children.TryGetValue(new YamlScalarNode(name), out var v) ? v : null;

    private static string? Scalar(YamlMappingNode node, string name) =>
        Child(node, name) is YamlScalarNode s ? s.Value : null;
}
