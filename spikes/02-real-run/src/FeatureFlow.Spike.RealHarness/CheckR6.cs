using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Check R6: the agent's credentials are short-lived, scoped to one repository,
/// and expire.
///
/// <para>
/// The evidence is the credential the broker actually minted: its scope, its
/// expiry from the remote, and the fact that a request for a wider scope is
/// refused rather than granted. The token value is never printed.
/// </para>
/// </summary>
public static class CheckR6
{
    public static async Task<int> RunAsync(RunContext context, Action<string> say)
    {
        say("## R6: credentials are short-lived, scoped to one repository, and expire");
        say("");

        var failures = 0;

        var actor = Actor.Agent("claude-code").ForStep(context.RunId, "implement");
        var scope = CredentialScope.ForRepository(
            $"{RunContext.Owner}/{RunContext.Repository}",
            "contents:write",
            "pull_requests:write",
            "metadata:read");

        var issued = await context.Broker.IssueAsync(
            actor, GitHubAppCredentialBroker.TargetSystemName, scope.Text,
            TimeSpan.FromMinutes(30), CancellationToken.None);

        if (!issued.IsOk)
        {
            say($"  FAIL: the broker refused an in-scope request: " +
                $"{issued.Error!.Class}/{issued.Error.Reason} {issued.Error.Detail}");
            return 1;
        }

        var credential = issued.Value!;

        say($"  credential id:   {credential.Id}");
        say($"  target system:   {credential.TargetSystem}");
        say($"  scope:           {credential.Scope}");
        say($"  issued at:       {credential.IssuedAt:O}");
        say($"  expires at:      {credential.ExpiresAt:O}   (from the remote, not computed)");
        say($"  ttl:             {credential.Ttl.TotalMinutes:F1} min");
        say($"  secret:          {credential.Secret.Length} chars, value withheld " +
            $"({SecretScrubber.Fingerprint(credential.Secret)})");
        say($"  actor:           {credential.Actor}");
        say("  permissions:");
        foreach (var (name, access) in credential.Permissions.OrderBy(p => p.Key, StringComparer.Ordinal))
            say($"    {name}: {access}");
        say("");

        // Short-lived: under an hour, and the maximum the broker will grant.
        // Compared with a small tolerance, because the remote's expires_at is
        // truncated to the second and a 60-minute request can land a fraction
        // over 60.
        var shortLived = credential.Ttl <= TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(1);
        say($"  short-lived (ttl {credential.Ttl.TotalMinutes:F1} min <= 60): {shortLived}");
        if (!shortLived) failures++;

        // Scoped to one repository: the scope names exactly one.
        var oneRepo = credential.Scope.StartsWith(
            $"repo:{RunContext.Owner}/{RunContext.Repository}", StringComparison.Ordinal);
        say($"  scoped to one repository: {oneRepo}");
        if (!oneRepo) failures++;

        // Expires: assertable against a clock, without waiting an hour.
        var notExpiredNow = !credential.IsExpired(credential.IssuedAt.AddMinutes(1));
        var expiredAfter = credential.IsExpired(credential.ExpiresAt.AddMinutes(1));
        say($"  not expired 1 min after issue: {notExpiredNow}");
        say($"  expired 1 min past expiry:     {expiredAfter}");
        if (!notExpiredNow || !expiredAfter) failures++;

        // Refuse, never widen. A wider scope must be refused, not granted.
        say("");
        say("  refusal (a wider scope must be refused, never granted):");

        var wider = await context.Broker.IssueAsync(
            actor, GitHubAppCredentialBroker.TargetSystemName,
            $"repo:{RunContext.Owner}/{RunContext.Repository} contents:admin",
            TimeSpan.FromMinutes(30), CancellationToken.None);

        var refusedWider = !wider.IsOk
                           && wider.Error!.Class == ErrorClass.Work
                           && wider.Error.Reason == "forbidden";
        say($"    wider access level:  refused={refusedWider} " +
            $"{(wider.Error is null ? "" : $"({wider.Error.Class}/{wider.Error.Reason})")}");
        if (!refusedWider) failures++;

        var otherRepo = await context.Broker.IssueAsync(
            actor, GitHubAppCredentialBroker.TargetSystemName,
            "repo:ai-feature-flow/some-other-repo contents:write",
            TimeSpan.FromMinutes(30), CancellationToken.None);

        var refusedOther = !otherRepo.IsOk
                           && otherRepo.Error!.Class == ErrorClass.Work
                           && otherRepo.Error.Reason == "forbidden";
        say($"    another repository:  refused={refusedOther} " +
            $"{(otherRepo.Error is null ? "" : $"({otherRepo.Error.Class}/{otherRepo.Error.Reason})")}");
        if (!refusedOther) failures++;

        var tooLong = await context.Broker.IssueAsync(
            actor, GitHubAppCredentialBroker.TargetSystemName, scope.Text,
            TimeSpan.FromHours(8), CancellationToken.None);

        var refusedTtl = !tooLong.IsOk
                         && tooLong.Error!.Class == ErrorClass.Work
                         && tooLong.Error.Reason == "forbidden";
        say($"    ttl over the maximum: refused={refusedTtl} " +
            $"{(tooLong.Error is null ? "" : $"({tooLong.Error.Class}/{tooLong.Error.Reason})")}");
        if (!refusedTtl) failures++;

        say("");
        say($"  **{(failures == 0 ? "PASS" : $"FAIL ({failures})")}**");
        say("");
        return failures == 0 ? 0 : 1;
    }
}
