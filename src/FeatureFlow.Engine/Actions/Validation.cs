using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Validation;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>
/// <c>artifact.validate</c> (docs/04 section 10.3): each validator over its subjects, against the run's repositories
/// frozen at their work branch heads, with the run tree's artifact index and findings ledger. Writes the
/// <c>validation_report</c> and any report a validator offers as evidence; <c>valid</c> when every validator is.
/// A subject that does not exist and is not <c>optional</c>, one that cannot be read, or a validator no provider offers,
/// stops the step for a person.
/// </summary>
static class Validating
{
    sealed record SubjectSpec(string Name, string From, bool Optional);

    sealed record Entry(string Validator, IReadOnlyList<SubjectSpec> Subjects, IReadOnlyDictionary<string, string> Options);

    static ActionStop Inputs(string message) => ActionStop.CannotAnswer($"check 31 ({Checks.Title(Checks.StepInputs)}): {message}");

    public static async Task<ActionResult> ValidateAsync(ActionRun run, ProviderSet providers)
    {
        var entries = Entries(run.Inputs.GetValueOrDefault("validation"));
        var runId = run.Call.Step.RunId;
        var frozen = await FreezeAsync(run);
        var (index, types, hasParent) = await IndexAsync(run, runId);
        var findings = ActionRun.Must(await Records(run).QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = runId }, run.Ct),
            "read the findings ledger");

        var validations = new List<ValidationEntry>();
        var offered = new List<OfferedReport>();
        foreach (var entry in entries)
        {
            var (name, provider) = Find(providers, entry.Validator);
            var subjects = new List<ValidationSubject>();
            foreach (var spec in entry.Subjects)
                subjects.Add(await SubjectAsync(run, spec, types, hasParent));
            var result = await provider.ValidateAsync(new ValidationRequest(entry.Validator, subjects, entry.Options, frozen, index, findings), run.Ct);
            if (!result.Ok)
            {
                var error = result.Error!;
                if (error.Class == ProviderErrorClass.Infrastructure && !error.IsConfigurationFault)
                    throw new ActionInfrastructureException($"validator {entry.Validator}: {error.Reason}: {error.Message}");
                throw ActionStop.CannotAnswer($"validator {entry.Validator} ({name}) could not check its subjects: " +
                                              (error.IsConfigurationFault ? error.DescribeFault() : $"{error.Reason}: {error.Message}"));
            }

            var value = result.Value!;
            validations.Add(new ValidationEntry(entry.Validator, name, subjects, value));
            offered.AddRange(value.Reports);
            run.Events.Add($"{entry.Validator} ({name}): {(value.Valid ? "valid" : "invalid")}" +
                           (value.Issues.Count == 0 ? "" : $"; {string.Join("; ", value.Issues.Take(5).Select(i => $"{i.Severity} {i.Code}: {i.Message}"))}"));
        }

        var report = new ValidationReport(validations.All(v => v.Result.Valid), frozen, validations);
        var reference = await run.PutJsonAsync(report);
        var outcome = report.Valid ? "valid" : "invalid";

        // A promised artifact named as an offered report's type (std's trace_report) is that report; every other is the
        // validation report (LOG 173).
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var promised in run.Call.Contract.ArtifactsFor(outcome))
            artifacts[promised] = offered.LastOrDefault(r => ValidatorDescriptor.TypeName(r.Type) == promised)?.Artifact.Id ?? reference;
        foreach (var unkept in offered.Where(r => !artifacts.ContainsValue(r.Artifact.Id)))
            run.Events.Add($"the offered {unkept.Type} {unkept.Artifact.Id} is not kept: the step declares no artifact of that name for {outcome}");
        return ActionResult.Reported(new StepReport(outcome, run.Call.Contract.ZeroOutputs, artifacts, $"{run.Call.Step.StepId}: {outcome}"), run.Events);
    }

    static IRecordStoreProvider Records(ActionRun run) => run.Provider<IRecordStoreProvider>("record_store");

    /// <summary>The <c>validation</c> input: each entry's validator, subjects (a name, or a map with <c>name</c>, <c>from</c>, <c>optional</c>) and options.</summary>
    static List<Entry> Entries(object? input)
    {
        if (input is not IList<object?> { Count: > 0 } list)
            throw Inputs("artifact.validate needs a list of validators in 'validation'");
        var entries = new List<Entry>();
        foreach (var item in list)
        {
            if (item is not IReadOnlyDictionary<string, object?> map || map.GetValueOrDefault("validator") is not string { Length: > 0 } validator)
                throw Inputs($"each entry of 'validation' is a map with a 'validator' name, not {YamlWriter.WritePlain(item)}");
            if (map.Keys.FirstOrDefault(k => k is not ("validator" or "subjects" or "options")) is { } unknown)
                throw Inputs($"validation entry {validator} has no key '{unknown}'; it takes validator, subjects and options");
            var subjects = new List<SubjectSpec>();
            foreach (var subject in map.GetValueOrDefault("subjects") as IList<object?> ?? [])
                subjects.Add(subject switch
                {
                    string name => new SubjectSpec(name, "current", false),
                    IReadOnlyDictionary<string, object?> s when s.GetValueOrDefault("name") is string name && s.Keys.All(k => k is "name" or "from" or "optional") =>
                        new SubjectSpec(name, From(s.GetValueOrDefault("from"), validator), s.GetValueOrDefault("optional") as bool? ?? false),
                    _ => throw Inputs($"a subject of {validator} is an artifact name, or a map of name, from and optional, not {YamlWriter.WritePlain(subject)}"),
                });
            var options = map.GetValueOrDefault("options") switch
            {
                null => new Dictionary<string, string>(),
                IReadOnlyDictionary<string, object?> o => o.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value), StringComparer.Ordinal),
                var other => throw Inputs($"the options of {validator} are a map, not {YamlWriter.WritePlain(other)}"),
            };
            entries.Add(new Entry(validator, subjects, options));
        }

        return entries;
    }

    /// <summary>A subject's <c>from</c>, as <c>consumes</c> writes it: <c>current</c>, <c>parent</c>, <c>root</c>, or <c>{ child: &lt;step&gt; }</c>.</summary>
    static string From(object? from, string validator) => from switch
    {
        null => "current",
        "current" or "parent" or "root" => (string)from,
        IReadOnlyDictionary<string, object?> { Count: 1 } child when child.GetValueOrDefault("child") is string step => "child:" + step,
        _ => throw Inputs($"a subject of {validator} comes from current, parent, root or {{ child: <step> }}, not {YamlWriter.WritePlain(from)}"),
    };

    /// <summary>
    /// The run's repositories at their work branch heads: every one the run has prepared (its work branch exists). A
    /// repository no step has touched has no work to check, and is left out.
    /// </summary>
    static async Task<IReadOnlyDictionary<string, string>> FreezeAsync(ActionRun run)
    {
        var frozen = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in run.Scope.Repositories)
            if (ActionRun.Must(await run.Vcs.ResolveAsync(new RepoRef(name), run.WorkReference, run.Ct), "read the work branch") is { } head)
                frozen[name] = head;
        run.Events.Add(frozen.Count == 0
            ? "no repository has a work branch yet; validators see no frozen revision"
            : "frozen: " + string.Join(", ", frozen.Select(kv => $"{kv.Key} at {EngineActions.Short(kv.Value)}")));
        return frozen;
    }

    /// <summary>
    /// The artifact index validators get: this run's artifacts as the workflow holds them, then its descendants' from the
    /// record store. Also every artifact of the run tree by reference, for the declared type of a subject from another run.
    /// </summary>
    static async Task<(List<ArtifactRecord> Index, Dictionary<string, ArtifactRecord> ByRef, bool HasParent)> IndexAsync(ActionRun run, string runId)
    {
        var records = Records(run);
        async Task<IReadOnlyList<ArtifactRecord>> Of(string id) =>
            ActionRun.Must(await records.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = id }, run.Ct), "read the artifact index");

        var index = run.Scope.Artifacts.Select(a => new ArtifactRecord(runId, a.StepId, 0, a.Name, a.Type, a.Ref, a.Action, a.Evidence, DateTimeOffset.MinValue)).ToList();
        index.AddRange((await Of(runId)).Where(a => a.RunId != runId));

        var root = runId;
        for (var hops = 0; hops < 32; hops++)
        {
            var record = ActionRun.Must(await records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = root }, run.Ct), "read the run").FirstOrDefault();
            if (record?.ParentRunId is not { } parent)
                break;
            root = parent;
        }

        var byRef = new Dictionary<string, ArtifactRecord>(StringComparer.Ordinal);
        foreach (var record in (root == runId ? [] : await Of(root)).Concat(index))
            byRef[record.Ref] = record;
        return (index, byRef, root != runId);
    }

    /// <summary>
    /// A subject as the validator gets it: the artifact the step consumed under the subject's name and source, with its
    /// declared type; a set (made by <c>artifact.compose</c>) with its members. Absent when it does not exist and is
    /// optional (LOG 63).
    /// </summary>
    static async Task<ValidationSubject> SubjectAsync(ActionRun run, SubjectSpec spec, IReadOnlyDictionary<string, ArtifactRecord> types, bool hasParent)
    {
        var key = spec.From == "current" ? spec.Name : $"{spec.From.Replace(':', '/')}/{spec.Name}";
        var reference = run.Scope.Consumed is { } consumed
            ? consumed.GetValueOrDefault(key)
            : spec.From == "current" ? run.Scope.Artifacts.LastOrDefault(a => a.Name == spec.Name)?.Ref : null;
        // Without the workflow's consumed artifacts, a subject from another run of a run that has one cannot be told absent;
        // passing it as absent would let requirement_inheritance pass a child with nothing to compare (LOG 173).
        if (reference is null && run.Scope.Consumed is null && spec.From != "current" && hasParent)
            throw ActionStop.CannotAnswer($"subject {key} is from another run, and this engine does not resolve it for actions yet");
        if (reference is null)
            return spec.Optional
                ? new ValidationSubject(spec.Name, null, null, spec.From)
                : throw ActionStop.CannotAnswer($"subject {key} does not exist, and is not optional");

        string text;
        try
        {
            text = await run.ReadTextAsync(reference);
        }
        catch (ActionStop stop)
        {
            throw ActionStop.CannotAnswer($"subject {key} ({reference}) cannot be read: {stop.Message}");
        }

        var record = types.GetValueOrDefault(reference);
        if (record?.Action != "artifact.compose")
            return new ValidationSubject(spec.Name, record?.Type, new ArtifactRef(reference), spec.From);
        var set = System.Text.Json.JsonSerializer.Deserialize<ArtifactSet>(text, ArtifactShapes.Engine)
                  ?? throw ActionStop.CannotAnswer($"subject {key} is not an artifact set");
        return new ValidationSubject(spec.Name, record.Type, new ArtifactRef(reference), spec.From,
            set.Members.Select(m => new ValidationSubject(m.Name, m.Type, new ArtifactRef(m.Ref), spec.From)).ToList());
    }

    /// <summary>The validator provider instance that offers a validator: the default first, then the others by name.</summary>
    static (string Name, IValidatorProvider Provider) Find(ProviderSet providers, string validator)
    {
        var offered = new List<string>();
        foreach (var (name, instance) in providers.Instances("validator"))
            if (instance is IValidatorProvider provider)
            {
                var names = provider.Validators().Select(v => v.Name).ToList();
                if (names.Contains(validator))
                    return (name, provider);
                offered.AddRange(names);
            }

        throw ActionStop.CannotAnswer(offered.Count == 0
            ? $"artifact.validate needs a validator provider for {validator}, and none is configured"
            : $"no validator provider offers {validator} (offered: {string.Join(", ", offered.Distinct().Order(StringComparer.Ordinal))})");
    }
}
