using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;

namespace FeatureFlow.Interpreter.Tests;

/// <summary><c>role_mapping</c> in the run's policy (LOG 267): a role's own entry, else <c>default</c>, else none.</summary>
public sealed class RoleMappingTests
{
    static RunPolicy Policy(Dictionary<string, AgentRoleMapping>? mapping) => new(3, 3, 40, 5, null, []) { RoleMapping = mapping };

    [Fact]
    public void A_role_takes_its_own_entry_then_the_default_then_none()
    {
        var reviewer = new AgentRoleMapping("claude_code", "opus");
        var fallback = new AgentRoleMapping("copilot_cli", null);
        var both = Policy(new() { ["reviewer"] = reviewer, ["default"] = fallback });
        var ownOnly = Policy(new() { ["reviewer"] = reviewer });

        Assert.Equal(reviewer, both.AgentFor("reviewer"));
        Assert.Equal(fallback, both.AgentFor("implementer"));
        Assert.Equal(fallback, both.AgentFor(null));
        Assert.Null(ownOnly.AgentFor("implementer"));
        Assert.Null(Policy(null).AgentFor("reviewer"));
    }
}
