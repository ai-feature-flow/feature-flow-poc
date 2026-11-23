namespace FeatureFlow.Api.Contracts;

/// <summary><c>GET /runs/{id}/tree</c>: the run tree that holds the run, from its root.</summary>
public sealed record RunTreeView(RunTreeNode Root);

/// <summary>
/// A run in a tree: the run, its plan (or, when it has none to show, why in <see cref="PlanNote"/>), its open waits, and its
/// child runs, each nested the same way.
/// </summary>
public sealed record RunTreeNode(
    RunView Run,
    PlanView? Plan,
    string? PlanNote,
    IReadOnlyList<WaitView> Waits,
    IReadOnlyList<RunTreeNode> Children);
