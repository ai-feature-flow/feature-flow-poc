using FeatureFlow.Model.Plans;

namespace FeatureFlow.Model.Tests;

/// <summary>
/// Which step an expression's <c>steps.</c> path names (docs/03 section 5): <see cref="Plan.FindStep"/> and
/// <see cref="Plan.LongestStepPrefix"/>, shared by the load check and the interpreter.
/// </summary>
public class PlanLookupTests
{
    static readonly SourceLocation At = new("plan.yaml", 1, 1);
    static readonly DocumentInfo Document = new() { Kind = "template", Name = "t", File = "plan.yaml" };
    static readonly Scope Root = new() { Id = "", Prefix = "", Kind = ScopeKind.Template, Document = Document };
    static readonly Scope Branch = new() { Id = "par.0", Prefix = "par.0.", Kind = ScopeKind.Branch, Parent = Root, Document = Document };
    static readonly Scope Fragment = new() { Id = "f", Prefix = "f.", Kind = ScopeKind.Fragment, Parent = Root, Document = Document };

    static Step S(string id, string local, Scope scope, StepForm form = StepForm.Action) =>
        new() { Id = id, LocalId = local, Form = form, Scope = scope, Location = At };

    /// <summary>
    /// Top level: <c>a</c>, <c>a.b</c>, <c>x</c>, and a parallel <c>par</c> whose branch holds <c>par.0.x</c>; a fragment
    /// used as <c>f</c> holds <c>f.y</c>.
    /// </summary>
    static Plan Plan()
    {
        var plan = new Plan { Document = Document, Root = Root };
        var container = new Container { Mode = "parallel" };
        var branch = new Branch { Index = 0, Scope = Branch };
        branch.Steps.Add(S("par.0.x", "x", Branch));
        container.Branches.Add(branch);
        var par = S("par", "par", Root, StepForm.Parallel);
        par.Container = container;
        plan.Steps.AddRange([S("a", "a", Root), S("a.b", "a.b", Root), S("x", "x", Root), par, S("f.y", "y", Fragment)]);
        return plan;
    }

    [Fact]
    public void A_step_is_found_in_its_branch_first_then_outwards_but_not_across_a_fragment()
    {
        var plan = Plan();

        Assert.Equal("par.0.x", plan.FindStep("x", Branch)?.Id);
        Assert.Equal("a", plan.FindStep("a", Branch)?.Id);
        Assert.Equal("x", plan.FindStep("x", Root)?.Id);
        Assert.Equal("f.y", plan.FindStep("y", Fragment)?.Id);
        Assert.Null(plan.FindStep("a", Fragment));
        Assert.Null(plan.FindStep("y", Root));
    }

    [Fact]
    public void A_steps_path_names_the_step_of_its_longest_prefix()
    {
        var plan = Plan();

        Assert.Equal(("a.b", 3), Named(plan.LongestStepPrefix(["steps", "a", "b", "outputs", "n"], Root)));
        Assert.Equal(("a", 2), Named(plan.LongestStepPrefix(["steps", "a", "outputs", "n"], Root)));
        Assert.Equal(("par.0.x", 2), Named(plan.LongestStepPrefix(["steps", "x", "outcome"], Branch)));
        Assert.Null(plan.LongestStepPrefix(["steps", "zz", "outcome"], Root));
    }

    static (string, int)? Named((Step Step, int FieldIndex)? found) => found is var (step, at) ? (step.Id, at) : null;
}
