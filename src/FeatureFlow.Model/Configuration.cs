using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model;

/// <summary>One named provider instance of a type (docs/07 section 2). <see cref="Kind"/> selects the implementation.</summary>
public sealed record ProviderInstanceConfig(string Type, string Name, string Kind, YMap Settings);

/// <summary>
/// A provider type's instances. <see cref="Default"/> is used unless a setting names another;
/// <see cref="DryRun"/> is the instance a <c>dry_run</c> run uses instead (docs/07 section 5).
/// </summary>
public sealed record ProviderTypeConfig(string Type, string? Default, string? DryRun, IReadOnlyDictionary<string, ProviderInstanceConfig> Instances);

/// <summary>API authentication. Version 1's solo profile: a local token (docs/02 section 7).</summary>
public sealed record ApiAuthConfig(string Mode, string? TokenRef, string Actor, IReadOnlyList<string> Roles);

/// <summary>
/// The agent worker's endpoint (docs/02 section 4.2; LOG 68, LOG 91): where it listens, the URLs at which sandboxes
/// reach its replicas, the model proxy's base URL as sandboxes see it, the secret every replica signs step tokens
/// with, and the sandbox client executable it copies into each sandbox.
/// </summary>
public sealed record AgentWorkerConfig(
    string? Listen,
    IReadOnlyList<string> Urls,
    string? ModelBaseUrl,
    string? TokenKeyRef,
    string? SandboxClient)
{
    public static readonly AgentWorkerConfig None = new(null, [], null, null, null);

    /// <summary>How often an agent's commits are pushed to the hidden reference. A fixed default, not a setting (LOG 91); tests shorten it.</summary>
    public TimeSpan CommitInterval { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>The installation layer (docs/07 section 2), as far as M3 reads it.</summary>
public sealed record InstallationConfig(
    string TemporalAddress,
    string TemporalNamespace,
    string TaskQueue,
    long ContinueAsNewEvents,
    IReadOnlyDictionary<string, ProviderTypeConfig> Providers,
    ApiAuthConfig ApiAuth,
    long DefaultAttempts,
    long DefaultRaces,
    string Location,
    bool Development = false,
    AgentWorkerConfig? AgentWorker = null);

/// <summary>A library source of the organisation layer (docs/08 section 3). M3 reads the <c>directory</c> source.</summary>
public sealed record LibrarySourceConfig(string Name, string Source, string Path);

/// <summary>A cap: a value, and whether a request may exceed it (docs/05 section 3.1).</summary>
public sealed record Cap<T>(T Value, bool Locked);

/// <summary>An approval rule (docs/05 section 3.2). <see cref="Auto"/> allows auto-resolution; a locked false refuses a template that asks for it.</summary>
public sealed record ApprovalRule(string Match, IReadOnlyList<string> Roles, long MinCount, bool FromChat, Cap<bool> Auto)
{
    public bool Matches(string type) => new DecisionTypeItem(Match, null, new SourceLocation("", 0, 0)).Matches(type) || Match == "*";
}

/// <summary>The policy the organisation layer sets, as far as M3 enforces it (docs/05 section 3).</summary>
public sealed record PolicyConfig(
    IReadOnlyList<ApprovalRule> Approval,
    long? AttemptsPerStep,
    long? AttemptsPerRun,
    IReadOnlyList<string>? AllowedRoles,
    Cap<bool>? RequireChecks,
    long HardPerStep = ConfigurationReader.DefaultHardPerStep,
    long HardPerRun = ConfigurationReader.DefaultHardPerRun)
{
    /// <summary>The first approval rule whose pattern matches the decision type, or null.</summary>
    public ApprovalRule? RuleFor(string type) => Approval.FirstOrDefault(r => r.Matches(type));
}

/// <summary>The organisation layer (docs/07 section 3), as far as M3 reads it.</summary>
public sealed record OrganisationConfig(string Name, IReadOnlyList<LibrarySourceConfig> Libraries, PolicyConfig Policy, string Location);

/// <summary>
/// Reads installation and organisation files, refusing unknown keys (docs/07 section 1: a key set where it
/// may not be is an error, never ignored). Keys the design defines and M3 does not yet read are refused with
/// a message saying so, so a file never appears to configure something that has no effect.
/// </summary>
public static class ConfigurationReader
{
    public static readonly IReadOnlyList<string> ProviderTypes =
    [
        "sandbox", "agent_runtime", "vcs", "code_host", "ci", "tracker", "findings_source", "validator", "toolset",
        "notifications", "session_surface", "artifact_store", "record_store", "secrets", "credential_broker",
        // M3 only (LOG 81): the stand-in for the actions later milestones build.
        "action_stand_in",
    ];

    const long DefaultHandOverEvents = 5120;

    /// <summary>
    /// The lowest hand-over threshold a run may have. One resume writes some 20 events before its first step
    /// (start, loading the lock, the first record), so a threshold near that would hand over without end.
    /// </summary>
    public const long MinimumHandOverEvents = 100;

    /// <summary>
    /// The hard attempt ceilings when the organisation sets none (F4; docs/05 section 5): a steerer may grant
    /// attempts up to them, an administrator beyond.
    /// </summary>
    public const long DefaultHardPerStep = 8;

    public const long DefaultHardPerRun = 400;

    public static InstallationConfig? ReadInstallation(string text, string file, DiagnosticBag d)
    {
        if (MapReader.Of(YamlText.Parse(text, file, d), d, "the installation") is not { } r)
            return null;
        Header(r, "installation");
        string address = "localhost:7233", ns = "default", queue = "feature-flow-orchestration";
        long events = DefaultHandOverEvents;
        if (MapReader.Of(r.Node("temporal"), d, "temporal") is { } t)
        {
            address = t.String("address") ?? address;
            ns = t.String("namespace") ?? ns;
            queue = t.String("task_queue") ?? queue;
            events = t.Int("continue_as_new_events") ?? events;
            if (events < MinimumHandOverEvents)
                t.Error($"temporal.continue_as_new_events must be at least {MinimumHandOverEvents}, not {events}", t.Map.Location);
            t.Finish();
        }

        var providers = new Dictionary<string, ProviderTypeConfig>(StringComparer.Ordinal);
        if (MapReader.Of(r.Node("providers"), d, "providers") is { } p)
        {
            foreach (var (key, node) in p.Map.Entries)
            {
                p.Node(key.Text);
                if (!ProviderTypes.Contains(key.Text))
                {
                    p.Error($"unknown provider type '{key.Text}'", key.Location);
                    continue;
                }

                if (MapReader.Of(node, d, $"providers.{key.Text}") is not { } pt)
                    continue;
                var instances = new Dictionary<string, ProviderInstanceConfig>(StringComparer.Ordinal);
                if (MapReader.Of(pt.Node("instances"), d, $"providers.{key.Text}.instances") is { } inst)
                    foreach (var (name, settings) in inst.Map.Entries)
                    {
                        inst.Node(name.Text);
                        var map = settings as YMap ?? new YMap([], settings.Location);
                        var kind = map["provider"].ScalarText() ?? name.Text;
                        instances[name.Text] = new ProviderInstanceConfig(key.Text, name.Text, kind,
                            new YMap(map.Entries.Where(e => e.Key.Text != "provider").ToList(), map.Location));
                    }

                var def = pt.String("default");
                var dry = pt.String("dry_run");
                foreach (var named in new[] { def, dry })
                    if (named is not null && !instances.ContainsKey(named))
                        pt.Error($"providers.{key.Text} names instance '{named}', which is not in its instances", pt.Map.Location);
                pt.Finish();
                providers[key.Text] = new ProviderTypeConfig(key.Text, def ?? (instances.Count == 1 ? instances.Keys.First() : null), dry, instances);
            }
        }

        var auth = new ApiAuthConfig("local_token", null, "local", ["viewer", "approver", "steerer", "admin"]);
        if (MapReader.Of(r.Node("api"), d, "api") is { } api)
        {
            api.String("base_url");
            if (MapReader.Of(api.Node("auth"), d, "api.auth") is { } a)
            {
                var mode = a.Enum("mode", ["local_token", "oidc"], required: true) ?? "local_token";
                if (mode == "oidc")
                    a.Error("api.auth.mode oidc arrives in M7; M3 has local_token only", a.Map.Location);
                var roles = a.StringList("roles");
                auth = new ApiAuthConfig(mode, a.String("token_ref", required: true), a.String("actor") ?? "local", roles.Count == 0 ? auth.Roles : roles);
                a.Finish();
            }

            api.Finish();
        }

        long attempts = 3, races = 10;
        if (MapReader.Of(r.Node("defaults"), d, "defaults") is { } def2)
        {
            if (MapReader.Of(def2.Node("limits"), d, "defaults.limits") is { } l)
            {
                attempts = l.Int("attempts") ?? attempts;
                races = l.Int("races") ?? races;
                l.Finish();
            }

            def2.Finish();
        }

        var agentWorker = AgentWorkerConfig.None;
        if (MapReader.Of(r.Node("agent_worker"), d, "agent_worker") is { } aw)
        {
            var client = aw.String("sandbox_client");
            agentWorker = new AgentWorkerConfig(aw.String("listen"), aw.StringList("urls"), aw.String("model_base_url"), aw.String("token_key_ref"),
                client is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file) ?? ".", client)));
            if (agentWorker.Listen is not null && agentWorker.Urls.Count == 0)
                aw.Error("agent_worker.urls must list the URLs at which sandboxes reach the agent worker", aw.Map.Location);
            if (agentWorker.Listen is not null && agentWorker.TokenKeyRef is null)
                aw.Error("agent_worker.token_key_ref must name the secret every replica signs step tokens with", aw.Map.Location);
            aw.Finish();
        }

        foreach (var later in new[] { "workers", "observability" })
            if (r.Has(later))
            {
                r.Node(later);
                r.Error($"{later} is not read before M8; remove it", r.Map[later]!.Location);
            }

        // A development installation may configure the fake and in-memory providers and the action stand-in (LOG 81, LOG 107).
        var development = r.Bool("development") ?? false;
        if (!development)
            foreach (var (type, config) in providers)
                foreach (var instance in config.Instances.Values.Where(i => i.Kind is "fake" or "memory" || type == "action_stand_in"))
                    r.Error($"providers.{type}.instances.{instance.Name}: the {(type == "action_stand_in" ? "action stand-in" : "fake provider")} is for development only; set development: true", r.Map.Location);
        // A runtime without the engine's tools reports only through a file: a test setting (SP3 T3), not for production.
        if (!development && providers.TryGetValue("agent_runtime", out var runtimes))
            foreach (var instance in runtimes.Instances.Values.Where(i => i.Settings["engine_tools"].ScalarText() == "false"))
                r.Error($"providers.agent_runtime.instances.{instance.Name}: engine_tools: false is for development only; set development: true", r.Map.Location);
        r.Finish();
        return new InstallationConfig(address, ns, queue, events, providers, auth, attempts, races, file, development, agentWorker);
    }

    public static OrganisationConfig? ReadOrganisation(string text, string file, DiagnosticBag d)
    {
        if (MapReader.Of(YamlText.Parse(text, file, d), d, "the organisation") is not { } r)
            return null;
        Header(r, "organisation");
        var name = r.String("name", required: true) ?? "";
        var libraries = new List<LibrarySourceConfig>();
        foreach (var item in LibraryItemReader.Seq(r.Node("libraries"), d, "libraries"))
            if (MapReader.Of(item, d, "a library source") is { } l)
            {
                var source = l.Enum("source", ["directory", "git", "package"], required: true);
                if (source is "git" or "package")
                    l.Error($"the {source} library source arrives in M8; M3 reads directory", l.Map.Location);
                var lib = new LibrarySourceConfig(l.String("name", required: true) ?? "", source ?? "directory", l.String("path", required: true) ?? "");
                l.Finish();
                libraries.Add(lib);
            }

        var policy = new PolicyConfig([], null, null, null, null);
        if (MapReader.Of(r.Node("policy"), d, "policy") is { } p)
        {
            var rules = new List<ApprovalRule>();
            foreach (var item in LibraryItemReader.Seq(p.Node("approval"), d, "policy.approval"))
                if (MapReader.Of(item, d, "an approval rule") is { } a)
                {
                    rules.Add(new ApprovalRule(a.String("match", required: true) ?? "*", a.StringList("roles"), a.Int("min_count") ?? 1,
                        a.Bool("from_chat") ?? false, BoolCap(a, "auto", d) ?? new Cap<bool>(false, false)));
                    a.Finish();
                }

            long? perStep = null, perRun = null;
            long hardStep = DefaultHardPerStep, hardRun = DefaultHardPerRun;
            if (MapReader.Of(p.Node("attempts"), d, "policy.attempts") is { } at)
            {
                if (MapReader.Of(at.Node("ceiling"), d, "policy.attempts.ceiling") is { } c)
                {
                    perStep = c.Int("per_step");
                    perRun = c.Int("per_run");
                    c.Finish();
                }

                if (MapReader.Of(at.Node("hard_ceiling"), d, "policy.attempts.hard_ceiling") is { } h)
                {
                    hardStep = h.Int("per_step") ?? hardStep;
                    hardRun = h.Int("per_run") ?? hardRun;
                    h.Finish();
                }

                if (perStep > hardStep || perRun > hardRun)
                    at.Error("policy.attempts.ceiling may not exceed policy.attempts.hard_ceiling", at.Map.Location);

                at.Finish();
            }

            IReadOnlyList<string>? allowed = null;
            if (MapReader.Of(p.Node("planning"), d, "policy.planning") is { } pl)
            {
                allowed = pl.Has("allowed_roles") ? pl.StringList("allowed_roles") : null;
                pl.Finish();
            }

            Cap<bool>? requireChecks = null;
            if (MapReader.Of(p.Node("merge"), d, "policy.merge") is { } m)
            {
                requireChecks = BoolCap(m, "require_checks", d);
                m.Finish();
            }

            foreach (var later in new[] { "budgets", "toolsets", "overrides" })
                if (p.Has(later))
                {
                    p.Node(later);
                    p.Error($"policy.{later} is not enforced before M7; remove it", p.Map[later]!.Location);
                }

            p.Finish();
            policy = new PolicyConfig(rules, perStep, perRun, allowed, requireChecks, hardStep, hardRun);
        }

        foreach (var later in new[] { "library_overrides", "library_inputs", "roles", "role_mapping", "notifications", "timeouts", "teams" })
            if (r.Has(later))
            {
                r.Node(later);
                r.Error($"{later} is not read before M7; remove it", r.Map[later]!.Location);
            }

        r.Finish();
        return new OrganisationConfig(name, libraries, policy, file);
    }

    static void Header(MapReader r, string kind)
    {
        var version = r.String("api_version", required: true);
        if (version is not null and not LibraryItemReader.ApiVersion)
            r.Error($"api_version must be {LibraryItemReader.ApiVersion}", r.Map.Location);
        var k = r.String("kind", required: true);
        if (k is not null && k != kind)
            r.Error($"kind must be {kind}, not '{k}'", r.Map.Location);
    }

    /// <summary>A boolean cap: a bare value (unlocked) or <c>{ value, locked }</c>.</summary>
    static Cap<bool>? BoolCap(MapReader r, string key, DiagnosticBag d)
    {
        if (!r.Has(key))
            return null;
        if (r.Map[key] is YMap)
        {
            var c = MapReader.Of(r.Node(key), d, key)!;
            var value = c.Bool("value");
            var locked = c.Bool("locked") ?? false;
            c.Finish();
            if (value is null)
                c.Error($"{key} needs a value", c.Map.Location);
            return value is null ? null : new Cap<bool>(value.Value, locked);
        }

        return r.Bool(key) is { } b ? new Cap<bool>(b, false) : null;
    }
}
