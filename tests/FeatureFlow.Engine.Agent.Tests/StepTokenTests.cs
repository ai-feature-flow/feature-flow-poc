namespace FeatureFlow.Engine.Agent.Tests;

/// <summary>Per-step tokens (docs/02 section 4.2): any replica holding the shared key checks them; nothing else does.</summary>
public sealed class StepTokenTests
{
    static readonly StepClaims Claims = new("run-1/implement/1", "run-1", "run-1", "implement", "branch", ["read", "edit"],
        DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds());

    [Fact]
    public void A_replica_with_the_same_key_reads_the_claims()
    {
        var token = StepTokens.FromSecret("shared").Issue(Claims);
        var other = StepTokens.FromSecret("shared").Check(token);
        Assert.Equal(Claims.StepKey, other?.StepKey);
        Assert.Equal(Claims.Tools, other?.Tools);
    }

    [Fact]
    public void A_token_signed_with_another_key_is_refused() =>
        Assert.Null(StepTokens.FromSecret("other").Check(StepTokens.FromSecret("shared").Issue(Claims)));

    [Fact]
    public void A_token_whose_claims_were_changed_is_refused()
    {
        var tokens = StepTokens.FromSecret("shared");
        var token = tokens.Issue(Claims);
        var widened = tokens.Issue(Claims with { WriteAccess = "none" }).Split('.')[1];
        var forged = string.Join('.', token.Split('.')[0], widened, token.Split('.')[2]);
        Assert.Null(tokens.Check(forged));
        Assert.NotNull(tokens.Check(token));
    }

    [Fact]
    public void An_expired_token_is_refused()
    {
        var tokens = StepTokens.FromSecret("shared");
        Assert.Null(tokens.Check(tokens.Issue(Claims with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds() })));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer x")]
    [InlineData("ffs1.not-base64!.x")]
    public void Garbage_is_refused(string? token) => Assert.Null(StepTokens.FromSecret("shared").Check(token));
}
