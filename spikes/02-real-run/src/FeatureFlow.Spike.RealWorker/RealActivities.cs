using FeatureFlow.Spike.Interpreter;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// <see cref="ISpikeActivities"/> against the real providers. This is the seam
/// Spike 1 left: the workflow is unchanged, and everything that touches the
/// outside world goes through a real provider.
///
/// <para>
/// The mapping from the fake sandbox:
/// </para>
/// <list type="bullet">
/// <item><c>POST /commands</c> + idempotency key becomes
/// <see cref="FeatureFlow.Spike.AgentRuntime.IAgentRuntimeProvider.StartAsync"/>
/// and <c>ResumeAsync</c>.</item>
/// <item>the <c>GET /commands/{handle}</c> poll loop becomes
/// <c>GetProgressAsync</c> inside one long-running activity, heartbeating.</item>
/// <item><c>lastCommit</c> and <c>sessionTurns</c> become real git commits on the
/// branch and the real session turn count.</item>
/// <item>generic <c>notify.send</c> steps become code host and CI calls.</item>
/// </list>
/// </summary>
public sealed partial class RealActivities : ISpikeActivities
{
    private readonly RunContext context;

    public RealActivities(RunContext context)
    {
        this.context = context;
    }

    /// <summary>Everything one run needs: plan, repository, providers.</summary>
    public RunContext Context => context;
}
