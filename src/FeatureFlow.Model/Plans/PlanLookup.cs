namespace FeatureFlow.Model.Plans;

/// <summary>Finding the step an expression names, as the load check and the interpreter both do.</summary>
public sealed partial class Plan
{
    Dictionary<string, Step>? byId;

    /// <summary>The step with this full id, else null.</summary>
    public Step? StepById(string id) => (byId ??= Index()).GetValueOrDefault(id);

    /// <summary>
    /// A step named in an expression written in <paramref name="scope"/>: in the branch, then the enclosing branches out to
    /// the document. The index is built on first use, so the plan must be complete by then, as the loader returns it.
    /// </summary>
    public Step? FindStep(string local, Scope scope)
    {
        for (var s = scope; ; s = s.Parent!)
        {
            if (StepById(s.Prefix + local) is { } step)
                return step;
            if (s.Kind != ScopeKind.Branch)
                return null;
        }
    }

    /// <summary>
    /// The step a <c>steps.</c> path names, by the longest run of the segments after <c>steps</c> that is a step
    /// (<c>steps.a.b.outputs.x</c> names step <c>a.b</c> if there is one), and the position of the field after the step id.
    /// </summary>
    public (Step Step, int FieldIndex)? LongestStepPrefix(IReadOnlyList<string> path, Scope scope)
    {
        for (var k = path.Count - 1; k >= 1; k--)
            if (FindStep(string.Join(".", path.Skip(1).Take(k)), scope) is { } step)
                return (step, k + 1);
        return null;
    }

    Dictionary<string, Step> Index()
    {
        var index = new Dictionary<string, Step>();
        foreach (var step in AllSteps)
            index.TryAdd(step.Id, step);
        return index;
    }
}
