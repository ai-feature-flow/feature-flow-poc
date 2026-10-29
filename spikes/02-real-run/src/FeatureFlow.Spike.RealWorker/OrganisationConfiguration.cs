using System.Text.RegularExpressions;

namespace FeatureFlow.Spike.RealWorker;

public sealed record ApprovalPolicy(
    IReadOnlyList<string> Roles,
    int MinCount,
    bool AgentCounts);

/// <summary>
/// The organisation facts needed at run start and at policy-enforcing steps.
/// The spike keeps a line-oriented reader, matching the fixed configuration
/// readers already used for the plan and repository manifest.
/// </summary>
public sealed class OrganisationConfiguration
{
    public const string DefaultBranchMergeDecision = "default_branch.merge_approval";
    public const string TicketMergeDecision = "ticket.merge_approval";

    public Dictionary<string, ApprovalPolicy> Approval { get; } =
        new(StringComparer.Ordinal);

    public HashSet<string> ChatAllowedDecisions { get; } =
        new(StringComparer.Ordinal);

    public HashSet<string> ReplanAllowedRoles { get; } =
        new(StringComparer.Ordinal);

    public Dictionary<string, int> AttemptCeilings { get; } =
        new(StringComparer.Ordinal);

    public int? PerRunAttemptCeiling { get; private set; }

    public bool HasCodeHost { get; private set; }
    public bool HasCi { get; private set; }
    public bool HasAgentRuntime { get; private set; }

    public bool SupportsCapability(string capability) => capability switch
    {
        "sandbox" => true,
        "agent_runtime" => HasAgentRuntime,
        "code_host" => HasCodeHost,
        "ci" => HasCi,
        _ => false,
    };

    public static OrganisationConfiguration Load(string? path = null)
    {
        path ??= FindConfigurationFile();
        if (path is null)
        {
            throw new FileNotFoundException(
                "the organisation configuration was not found; looked for " +
                "plans/organisation.yaml walking up from " + AppContext.BaseDirectory);
        }

        return Parse(File.ReadAllLines(path));
    }

    private static OrganisationConfiguration Parse(IEnumerable<string> lines)
    {
        var result = new OrganisationConfiguration();
        var inPolicy = false;
        string? subsection = null;

        foreach (var raw in lines)
        {
            var line = raw.Split('#', 2)[0].TrimEnd('\r', ' ');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var indent = line.Length - line.TrimStart().Length;
            var text = line.Trim();

            if (indent == 0)
            {
                result.HasCodeHost |= text == "codeHost:";
                result.HasCi |= text == "ci:";
                result.HasAgentRuntime |= text == "agentRuntime:";
                inPolicy = text == "policy:";
                subsection = null;
                continue;
            }

            if (!inPolicy)
                continue;

            if (indent == 2 && text.EndsWith(':'))
            {
                subsection = text[..^1];
                continue;
            }

            if (indent != 4 || subsection is null)
                continue;

            if (subsection == "approval")
            {
                var colon = text.IndexOf(':');
                if (colon <= 0)
                    continue;

                var decisionType = text[..colon].Trim();
                var inline = text[(colon + 1)..];
                result.Approval[decisionType] = new ApprovalPolicy(
                    ListField(inline, "roles"),
                    IntField(inline, "minCount") ?? 1,
                    BoolField(inline, "agentCounts") ?? false);
            }
            else if (subsection == "attempts" && text.StartsWith("ceiling:", StringComparison.Ordinal))
            {
                var inline = text["ceiling:".Length..];
                foreach (Match match in Regex.Matches(inline, @"([A-Za-z][A-Za-z0-9_.-]*)\s*:\s*(\d+)"))
                {
                    var key = match.Groups[1].Value;
                    var value = int.Parse(match.Groups[2].Value);
                    if (key == "perRun")
                        result.PerRunAttemptCeiling = value;
                    else
                        result.AttemptCeilings[key] = value;
                }
            }
            else if (subsection == "replan" && text.StartsWith("allowed_roles:", StringComparison.Ordinal))
            {
                foreach (var role in ParseList(text["allowed_roles:".Length..]))
                    result.ReplanAllowedRoles.Add(role);
            }
            else if (subsection == "chat" && text.StartsWith("allowedDecisions:", StringComparison.Ordinal))
            {
                foreach (var decision in ParseList(text["allowedDecisions:".Length..]))
                    result.ChatAllowedDecisions.Add(decision);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ListField(string text, string name)
    {
        var match = Regex.Match(text, $@"\b{Regex.Escape(name)}\s*:\s*\[([^\]]*)\]");
        return match.Success ? ParseList(match.Groups[1].Value) : Array.Empty<string>();
    }

    private static int? IntField(string text, string name)
    {
        var match = Regex.Match(text, $@"\b{Regex.Escape(name)}\s*:\s*(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static bool? BoolField(string text, string name)
    {
        var match = Regex.Match(text, $@"\b{Regex.Escape(name)}\s*:\s*(true|false)", RegexOptions.IgnoreCase);
        return match.Success ? bool.Parse(match.Groups[1].Value) : null;
    }

    private static List<string> ParseList(string text) => text
        .Trim().Trim('[', ']')
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(value => value.Trim('"', '\''))
        .Where(value => value.Length > 0)
        .ToList();

    private static string? FindConfigurationFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "plans", "organisation.yaml");
            if (File.Exists(candidate))
                return candidate;

            var viaSrc = Path.Combine(directory.FullName, "src", "plans", "organisation.yaml");
            if (File.Exists(viaSrc))
                return viaSrc;

            directory = directory.Parent;
        }

        return null;
    }
}
