using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FeatureFlow.Spike.CodeHost;
using FeatureFlow.Spike.CodeHostHarness;
using FeatureFlow.Spike.Contracts;

// Spike 2 verification harness for the GitHub code host provider.
//
// Runs against the REAL ai-feature-flow/feature-flow-example repository.
// Every check prints real output; nothing here is simulated except the
// transport failure in check 5, which is labelled as such.
//
// Usage:
//   dotnet run --project FeatureFlow.Spike.CodeHostHarness -- --token <installation-token>
//
// Mint a token first:
//   python tools/gh_app_token.py --app-id 5507595 \
//     --key D:\Users\Stuart\keys\ai-feature-flow-agent-access.2026-10-28.private-key.pem \
//     --installation 176108462 --emit

var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? token = null;

for (var i = 0; i < cliArgs.Length; i++)
{
    if (cliArgs[i] == "--token" && i + 1 < cliArgs.Length) { token = cliArgs[i + 1]; i++; }
    else if (cliArgs[i].StartsWith("--token=", StringComparison.Ordinal))
        token = cliArgs[i]["--token=".Length..];
}

if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("error: --token is required");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Mint one with:");
    Console.Error.WriteLine("  python tools\\gh_app_token.py --app-id 5507595 --key <key.pem> --installation 176108462 --emit");
    return 2;
}

var repo = new RepoRef("ai-feature-flow", "feature-flow-example");
var runId = $"spike2-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
var failures = new List<string>();

Console.WriteLine("=== Spike 2: GitHub code host provider verification ===");
Console.WriteLine($"repository: {repo}");
Console.WriteLine($"run id:     {runId}");
Console.WriteLine($"utc:        {DateTimeOffset.UtcNow:O}");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Check 1: CheckAsync succeeds and reports real capabilities
// ---------------------------------------------------------------------------
Console.WriteLine("--- Check 1: CheckAsync and capability declaration ---");

var provider = new GitHubCodeHostProvider(
    new StaticTokenFactory(token),
    new InMemoryArtifactStore());

var descriptor = provider.Describe();
Console.WriteLine($"name:        {descriptor.Name}");
Console.WriteLine($"version:     {descriptor.Version}");
Console.WriteLine($"schema:      {descriptor.ConfigSchemaRef}");
Console.WriteLine($"requires:    {(descriptor.Requires.Count == 0 ? "(none)" : string.Join(", ", descriptor.Requires))}");

var capabilities = descriptor.Capabilities.OfType<CodeHostCapabilities>().FirstOrDefault();
if (capabilities is null)
{
    Fail("Check 1", "no CodeHostCapabilities on the descriptor");
}
else
{
    Console.WriteLine("capabilities:");
    Console.WriteLine($"  PullRequest:      {capabilities.PullRequest}");
    Console.WriteLine($"  ReviewComments:   {capabilities.ReviewComments}");
    Console.WriteLine($"  CheckStatus:      {capabilities.CheckStatus}");
    Console.WriteLine($"  MergeMethods:     {string.Join(", ", capabilities.MergeMethods)}");
    Console.WriteLine($"  BranchProtection: {capabilities.BranchProtection}");
    Console.WriteLine($"  MergeQueue:       {capabilities.MergeQueue}");
    Console.WriteLine($"  InboundEvents:    {capabilities.InboundEvents}");
}

var check = await provider.CheckAsync(CancellationToken.None);
if (check.IsOk)
{
    Console.WriteLine($"CheckAsync:  OK");
    Console.WriteLine($"  detail:    {check.Value!.Detail}");
    Console.WriteLine($"  latency:   {check.Value.Latency?.TotalMilliseconds:F0} ms");
}
else
{
    Fail("Check 1", $"CheckAsync failed: {check.Error!.Class}/{check.Error.Reason} {check.Error.RemoteId}");
}

Console.WriteLine();

// ---------------------------------------------------------------------------
// Check 2: branch creation is idempotent by key
// ---------------------------------------------------------------------------
Console.WriteLine("--- Check 2: branch creation, twice with the same idempotency key ---");

var branchKey = Marker.Key(runId, "scm.create_branch", repo.FullName, "main");
var branchName = $"ff/ticket/spike2-{Marker.BranchSuffix(branchKey)}";
Console.WriteLine($"idempotency key: {branchKey}");
Console.WriteLine($"marker:          {Marker.For(branchKey)}");
Console.WriteLine($"branch:          {branchName}");

var first = await provider.CreateBranchAsync(repo, branchName, "main", branchKey, CancellationToken.None);
if (first.IsOk)
{
    Console.WriteLine($"create #1:       OK  head={first.Value!.HeadSha}");
}
else
{
    Fail("Check 2", $"create #1 failed: {first.Error!.Class}/{first.Error.Reason} {first.Error.RemoteId}");
}

var second = await provider.CreateBranchAsync(repo, branchName, "main", branchKey, CancellationToken.None);
if (second.IsOk)
{
    Console.WriteLine($"create #2:       OK  head={second.Value!.HeadSha}");
}
else
{
    Fail("Check 2", $"create #2 failed: {second.Error!.Class}/{second.Error.Reason} {second.Error.RemoteId}");
}

if (first.IsOk && second.IsOk)
{
    var same = string.Equals(first.Value!.HeadSha, second.Value!.HeadSha, StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"adopted, not duplicated: {same}");
    if (!same) Fail("Check 2", "second create returned a different head SHA");
}

// Prove there is exactly one branch, not two.
var branchCount = await CountBranchesAsync(provider, repo, branchName);
Console.WriteLine($"branches matching name:  {branchCount}");
if (branchCount != 1) Fail("Check 2", $"expected exactly 1 branch, found {branchCount}");

Console.WriteLine();

// ---------------------------------------------------------------------------
// Check 3: read feature-flow.yaml from the branch and record its hash (R16)
// ---------------------------------------------------------------------------
Console.WriteLine("--- Check 3: read feature-flow.yaml from the branch, record hash (R16) ---");

var manifest = await provider.ReadFileAsync(repo, "feature-flow.yaml", branchName, CancellationToken.None);
if (manifest.IsOk)
{
    var file = manifest.Value!;
    Console.WriteLine($"path:        {file.Path}");
    Console.WriteLine($"ref:         {file.Ref}");
    Console.WriteLine($"size:        {file.SizeBytes} bytes");
    Console.WriteLine($"blob sha:    {file.BlobSha}");
    Console.WriteLine($"content hash: sha256:{file.ContentHash}");
    Console.WriteLine("--- manifest content ---");
    Console.WriteLine(file.Content.TrimEnd());
    Console.WriteLine("--- end manifest ---");

    // The R16 check is only meaningful if the hash is stable across reads.
    var again = await provider.ReadFileAsync(repo, "feature-flow.yaml", branchName, CancellationToken.None);
    if (again.IsOk)
    {
        var stable = string.Equals(file.ContentHash, again.Value!.ContentHash, StringComparison.Ordinal);
        Console.WriteLine($"hash stable across reads: {stable}");
        if (!stable) Fail("Check 3", "manifest hash changed between two reads of the same ref");
    }
}
else
{
    Fail("Check 3", $"read failed: {manifest.Error!.Class}/{manifest.Error.Reason} {manifest.Error.RemoteId}");
}

Console.WriteLine();

// ---------------------------------------------------------------------------
// Check 4: FindByMarkerAsync returns empty, not not_found
// ---------------------------------------------------------------------------
Console.WriteLine("--- Check 4: FindByMarkerAsync for a marker that does not exist ---");

var absentKey = Marker.Key(runId, "does-not-exist", Guid.NewGuid().ToString("N"));
var absentMarker = Marker.For(absentKey);
Console.WriteLine($"marker:      {absentMarker}");

var found = await provider.FindByMarkerAsync(repo, absentMarker, CancellationToken.None);
if (found.IsOk)
{
    var result = found.Value!;
    Console.WriteLine($"result:      OK (not an error)");
    Console.WriteLine($"  branches:     {result.Branches.Count}");
    Console.WriteLine($"  pull requests:{result.PullRequests.Count}");
    Console.WriteLine($"  IsEmpty:      {result.IsEmpty}");

    if (!result.IsEmpty)
        Fail("Check 4", "expected empty result for an absent marker");
}
else
{
    Fail("Check 4", $"expected empty, got {found.Error!.Class}/{found.Error.Reason}");
}

// The positive case: the branch we created carries its marker, so the same
// search must find it. This is what makes the empty answer meaningful.
var present = await provider.FindByMarkerAsync(repo, Marker.For(branchKey), CancellationToken.None);
if (present.IsOk)
{
    Console.WriteLine($"control:     marker for the created branch found {present.Value!.Branches.Count} branch(es)");
    if (present.Value!.Branches.Count != 1)
        Fail("Check 4", "control search should find the branch we created");
}
else
{
    Fail("Check 4", $"control search failed: {present.Error!.Class}/{present.Error.Reason}");
}

Console.WriteLine();

// ---------------------------------------------------------------------------
// Check 5: error classification
// ---------------------------------------------------------------------------
Console.WriteLine("--- Check 5: error classification ---");

// 5a: a real 404 from the real API.
var missing = new RepoRef("ai-feature-flow", "feature-flow-example");
var missingBranch = await provider.ReadFileAsync(
    missing, "feature-flow.yaml", "refs/heads/no-such-branch-anywhere", CancellationToken.None);

if (missingBranch.IsOk)
{
    Fail("Check 5a", "expected a 404 for a branch that does not exist");
}
else
{
    var error = missingBranch.Error!;
    Console.WriteLine($"5a real 404:      class={error.Class} reason={error.Reason}");
    if (error.Class != ErrorClass.Work || error.Reason != "not_found")
        Fail("Check 5a", $"expected Work/not_found, got {error.Class}/{error.Reason}");
}

// 5b: a simulated transport failure, through the same provider code.
foreach (var (mode, expected) in new[]
         {
             (FailureMode.Connection, "unreachable"),
             (FailureMode.Timeout, "timeout"),
             (FailureMode.ServerError, "server_error"),
             (FailureMode.RateLimited, "rate_limited"),
         })
{
    var handler = new FailingHandler { Mode = mode, RetryAfter = TimeSpan.FromSeconds(42) };
    var failing = new GitHubCodeHostProvider(
        new StaticTokenFactory(token),
        new InMemoryArtifactStore(),
        new HttpClient(handler));

    var result = await failing.ReadFileAsync(repo, "feature-flow.yaml", "main", CancellationToken.None);

    if (result.IsOk)
    {
        Fail($"Check 5b/{mode}", "expected a failure");
        continue;
    }

    var error = result.Error!;
    var retry = error.RetryAfter is null ? "-" : $"{error.RetryAfter.Value.TotalSeconds:F0}s";
    Console.WriteLine($"5b {mode,-12}: class={error.Class} reason={error.Reason} retryAfter={retry}");

    if (error.Class != ErrorClass.Infrastructure)
        Fail($"Check 5b/{mode}", $"expected Infrastructure, got {error.Class}");

    if (error.Reason != expected)
        Fail($"Check 5b/{mode}", $"expected {expected}, got {error.Reason}");

    if (mode == FailureMode.RateLimited && error.RetryAfter != TimeSpan.FromSeconds(42))
        Fail($"Check 5b/{mode}", "Retry-After was not honoured");
}

Console.WriteLine();

// ---------------------------------------------------------------------------
// Cleanup: delete the branch we created
// ---------------------------------------------------------------------------
Console.WriteLine("--- Cleanup: delete the verification branch ---");

var deleted = await DeleteBranchAsync(provider, repo, branchName);
Console.WriteLine($"deleted {branchName}: {deleted}");
if (!deleted) Fail("Cleanup", "branch deletion failed; remove it manually");

var remaining = await CountBranchesAsync(provider, repo, branchName);
Console.WriteLine($"branches remaining: {remaining}");

Console.WriteLine();

// ---------------------------------------------------------------------------
// Summary
// ---------------------------------------------------------------------------
Console.WriteLine("=== Summary ===");
if (failures.Count == 0)
{
    Console.WriteLine("all checks passed");
    return 0;
}

Console.WriteLine($"{failures.Count} failure(s):");
foreach (var failure in failures)
    Console.WriteLine($"  - {failure}");

return 1;

void Fail(string check, string detail) => failures.Add($"{check}: {detail}");

static async Task<int> CountBranchesAsync(ICodeHostProvider provider, RepoRef repo, string name)
{
    var found = await provider.FindByMarkerAsync(repo, name, CancellationToken.None);
    return found.IsOk ? found.Value!.Branches.Count(b => b.Name == name) : -1;
}

static async Task<bool> DeleteBranchAsync(ICodeHostProvider provider, RepoRef repo, string name)
{
    var result = await provider.DeleteBranchAsync(repo, name, CancellationToken.None);
    return result.IsOk;
}
