using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model.Tests;

/// <summary>The installation and organisation readers (docs/07), and the writer that carries values and patches through Temporal.</summary>
public class ConfigurationTests
{
    const string Installation = """
        api_version: feature-flow/v1
        kind: installation
        temporal: { address: "localhost:7833", task_queue: ff-orchestration, continue_as_new_events: 400 }
        providers:
          record_store: { default: sqlite, instances: { sqlite: { path: records.db } } }
          agent_runtime: { default: scripted, dry_run: recording, instances: { scripted: { script: s.yaml }, recording: { folder: rec } } }
        api:
          auth: { mode: local_token, token_ref: secrets/api_token, actor: dev, roles: [approver] }
        defaults:
          limits: { attempts: 4 }
        """;

    [Fact]
    public void An_installation_reads_its_providers_and_defaults()
    {
        var d = new DiagnosticBag();
        var config = ConfigurationReader.ReadInstallation(Installation, "installation.yaml", d)!;
        Assert.Empty(d);
        Assert.Equal(("localhost:7833", "ff-orchestration", 400L, 4L), (config.TemporalAddress, config.TaskQueue, config.ContinueAsNewEvents, config.DefaultAttempts));
        var runtime = config.Providers["agent_runtime"];
        Assert.Equal(("scripted", "recording"), (runtime.Default, runtime.DryRun));
        Assert.Equal("s.yaml", runtime.Instances["scripted"].Settings["script"].ScalarText());
        Assert.Equal("dev", config.ApiAuth.Actor);
        Assert.Equal(["approver"], config.ApiAuth.Roles);
    }

    [Fact]
    public void An_installation_reads_the_agent_worker_s_endpoint()
    {
        var d = new DiagnosticBag();
        var config = ConfigurationReader.ReadInstallation(Installation.Replace("defaults:", """
            agent_worker:
              listen: "http://0.0.0.0:8490"
              urls: ["http://host.docker.internal:8490", "http://host.docker.internal:8491"]
              token_key_ref: secrets/step_token_key
              commit_interval: 10s
              sandbox_client: bin/ff-sandbox
            defaults:
            """, StringComparison.Ordinal), "/etc/ff/installation.yaml", d)!;
        Assert.Empty(d);
        var worker = config.AgentWorker!;
        Assert.Equal(2, worker.Urls.Count);
        Assert.Equal(("secrets/step_token_key", TimeSpan.FromSeconds(10)), (worker.TokenKeyRef, worker.CommitInterval));
        Assert.Equal(Path.GetFullPath("/etc/ff/bin/ff-sandbox"), worker.SandboxClient);
    }

    [Theory]
    [InlineData("defaults:", "agent_worker: { listen: \"http://0.0.0.0:8490\", token_key_ref: secrets/k }\ndefaults:", "agent_worker.urls must list")]
    [InlineData("defaults:", "agent_worker: { listen: \"http://0.0.0.0:8490\", urls: [\"http://h:1\"] }\ndefaults:", "agent_worker.token_key_ref must name")]
    [InlineData("defaults:", "agent_worker: { commit_interval: often }\ndefaults:", "commit_interval must be a duration")]
    [InlineData("  record_store: { default: sqlite, instances: { sqlite: { path: records.db } } }", "  record_stor: { instances: { sqlite: {} } }", "unknown provider type 'record_stor'")]
    [InlineData("  record_store: { default: sqlite,", "  record_store: { default: postgres,", "names instance 'postgres'")]
    [InlineData("defaults:", "workers: { mode: local }\ndefaults:", "workers is not read before M8")]
    [InlineData("  limits: { attempts: 4 }", "  limits: { attempts: 4, retries: 2 }", "unknown key 'retries'")]
    [InlineData("continue_as_new_events: 400", "continue_as_new_events: 50", "must be at least 100")]
    [InlineData("  record_store: { default: sqlite,", "  code_host: { instances: { fake: {} } }\n  record_store: { default: sqlite,", "for development only")]
    public void An_installation_refuses_what_it_does_not_read(string old, string replacement, string message)
    {
        var d = new DiagnosticBag();
        ConfigurationReader.ReadInstallation(Installation.Replace(old, replacement, StringComparison.Ordinal), "installation.yaml", d);
        Assert.Contains(d, x => x.Blocks && x.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void An_organisation_reads_libraries_caps_and_approval_rules()
    {
        var d = new DiagnosticBag();
        var org = ConfigurationReader.ReadOrganisation("""
            api_version: feature-flow/v1
            kind: organisation
            name: acme
            libraries: [{ name: std, source: directory, path: ../library/std }]
            policy:
              approval:
                - { match: "*.merge_action", roles: [approver], min_count: 2, auto: { value: false, locked: true } }
                - { match: "*", roles: [approver], auto: true }
              attempts: { ceiling: { per_step: 5, per_run: 40 }, hard_ceiling: { per_step: 8, per_run: 60 } }
              planning: { allowed_roles: [planner] }
              merge: { require_checks: { value: true, locked: true } }
            """, "organisation.yaml", d)!;
        Assert.Empty(d);
        Assert.Equal((5L, 40L), (org.Policy.AttemptsPerStep, org.Policy.AttemptsPerRun));
        Assert.Equal((8L, 60L), (org.Policy.HardPerStep, org.Policy.HardPerRun));
        Assert.Equal(new Cap<bool>(false, true), org.Policy.RuleFor("delivery.merge_action")!.Auto);
        Assert.Equal(new Cap<bool>(true, false), org.Policy.RuleFor("ticket.spec_approval")!.Auto);
        Assert.Equal(new Cap<bool>(true, true), org.Policy.RequireChecks);
        Assert.Equal(["planner"], org.Policy.AllowedRoles);
    }

    [Fact]
    public void A_written_node_reads_back_with_the_same_meaning()
    {
        var d = new DiagnosticBag();
        var node = YamlText.Parse("""
            { a: 1, b: "1", c: true, d: "true", e: ~, f: [x, "y: z", 2.5], g: { "h\"i": 'quote "it"' }, j: "" }
            """, "t", d)!;
        var again = YamlWriter.Read(YamlWriter.Write(node));
        Assert.Equal(node.ToPlain() is Dictionary<string, object?> m ? Flatten(m) : "", again.ToPlain() is Dictionary<string, object?> n ? Flatten(n) : "");
        Assert.Equal(1L, ((YMap)again)["a"]!.ToPlain());
        Assert.Equal("1", ((YMap)again)["b"]!.ToPlain());
        Assert.Equal("true", ((YMap)again)["d"]!.ToPlain());
    }

    static string Flatten(object? value) => value switch
    {
        Dictionary<string, object?> m => "{" + string.Join(",", m.Select(kv => kv.Key + ":" + Flatten(kv.Value))) + "}",
        List<object?> l => "[" + string.Join(",", l.Select(Flatten)) + "]",
        null => "null",
        _ => value.GetType().Name + ":" + value,
    };

    [Fact]
    public void A_written_patch_reads_back_as_the_same_patch()
    {
        var d = new DiagnosticBag();
        var patch = PatchReader.Read(YamlText.Parse("""
            api_version: feature-flow/v1
            kind: patch
            metadata: { proposed_by: { type: agent, step_id: plan }, reason: "two \"repos\"" }
            base_revision: 3
            resume_at: build
            changes:
              - { op: insert, after: plan, step: { id: extra, action: test.run, with: { tier_tags: [unit] } } }
              - { op: set, target: build, field: routes.passed, value: { end: completed } }
              - { op: chain, target: build, on_other: report, step: { id: more, action: notify.send } }
              - { op: remove_branch, target: lanes, branch: lanes_0 }
            """, "p", d)!, d)!;
        Assert.Empty(d);
        var again = PatchReader.ReadWritten(PatchReader.Write(patch));
        Assert.Equal(PatchReader.Write(patch), PatchReader.Write(again));
        Assert.Equal((patch.BaseRevision, patch.ResumeAt, patch.Reason, patch.ProposedBy), (again.BaseRevision, again.ResumeAt, again.Reason, again.ProposedBy));
        Assert.Equal(patch.Changes.Select(c => (c.Op, c.Target, c.Position, c.Field, c.OnOther, c.Branch)), again.Changes.Select(c => (c.Op, c.Target, c.Position, c.Field, c.OnOther, c.Branch)));
    }
}
