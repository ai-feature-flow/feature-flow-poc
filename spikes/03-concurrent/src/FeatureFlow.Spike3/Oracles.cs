using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike3;

/// <summary>
/// docs/17 3.4: before any review result is reported, check the integration
/// branch with a throwaway, uncommitted test. The clone is scratch; nothing is
/// pushed.
/// </summary>
public static class Oracles
{
    private const string OracleTest = """
        using FeatureFlow.Example.Api;
        using Xunit;

        namespace FeatureFlow.Example.Api.UnitTests;

        [Trait("Category", "unit")]
        public class Spike3OracleTests
        {
            [Fact]
            public void Defect_lookup_by_created_name_returns_null()
            {
                var store = new WidgetStore();
                store.Add("Alpha");
                Assert.Null(store.FindByName("Alpha"));
            }

            [Fact]
            public void Neg_contract_holds_for_case_and_whitespace()
            {
                var store = new WidgetStore();
                var created = store.Add("Alpha");
                Assert.Equal(created.Id, store.FindByName("Alpha")?.Id);
                Assert.Equal(created.Id, store.FindByName("  Alpha ")?.Id);
            }

            [Fact]
            public void Resolution_keeps_both_sides()
            {
                var store = new WidgetStore();
                var widget = store.Add("  Beta ");
                Assert.Equal("beta", widget.Name);
                Assert.True((System.DateTimeOffset.UtcNow - widget.CreatedAt).Duration() < System.TimeSpan.FromSeconds(1));
            }
        }
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var suffix = Cli.Arg(args, "--suffix") ?? "spike-3";
        var reference = Cli.Arg(args, "--ref") ?? $"origin/ff/epic/{suffix}";
        var patch = Cli.Arg(args, "--apply");
        var label = Cli.Arg(args, "--label") ?? "integration";
        var dir = Path.Combine(Cli.WorkDir, suffix, $"oracle-{label}");
        var clone = GitHubOps.Clone(dir);
        Cli.Git(clone, "checkout", "--quiet", "--detach", reference);
        var head = Cli.Git(clone, "rev-parse", "HEAD");
        if (patch is not null)
            Cli.Git(clone, "apply", Path.GetFullPath(patch));

        // The tier as committed, before the oracle file is added.
        var tier = Test(clone, "Category=unit");

        File.WriteAllText(Path.Combine(clone, "tests/FeatureFlow.Example.Api.UnitTests/Spike3OracleTests.cs"), OracleTest);
        var defect = Test(clone, "FullyQualifiedName~Defect_lookup_by_created_name_returns_null");
        var neg = Test(clone, "FullyQualifiedName~Neg_contract_holds_for_case_and_whitespace");
        var resolution = Test(clone, "FullyQualifiedName~Resolution_keeps_both_sides");

        var result = new Dictionary<string, object?>
        {
            ["ref"] = reference,
            ["head"] = head,
            ["applied"] = patch is null ? null : Path.GetFileName(patch),
            ["unitTier"] = tier,
            ["defectPresent"] = defect,
            ["contractHolds"] = neg,
            ["resolutionKeepsBothSides"] = resolution,
        };

        if (patch is null)
        {
            // The conflict oracle comes from the engine's own forward-merge records.
            var records = Path.Combine(Cli.WorkDir, suffix, "records");
            var forwards = Directory.Exists(records)
                ? Directory.GetFiles(records, "*.forward.jsonl").SelectMany(File.ReadAllLines)
                    .Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList()
                : new List<JsonElement>();
            var conflicts = forwards.Where(f => f.GetProperty("outcome").GetString() == "blocked").ToList();
            result["forwardMerges"] = forwards;
            result["conflictOnWidgetStore"] = conflicts.Any(f =>
                f.TryGetProperty("conflictedFiles", out var files) && files.ValueKind == JsonValueKind.Array &&
                files.EnumerateArray().Any(x => x.GetString()!.EndsWith("WidgetStore.cs")));
        }

        foreach (var (k, v) in result.Where(kv => kv.Key != "forwardMerges"))
            Cli.Log($"ORACLE {label} {k}: {JsonSerializer.Serialize(v)}");
        var outDir = Path.Combine(Cli.ResultsDir, suffix);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"oracle-{label}.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static object Test(string clone, string filter)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            WorkingDirectory = clone,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "test", "tests/FeatureFlow.Example.Api.UnitTests", "--filter", filter })
            psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        var text = output.Result;
        var summary = text.Split('\n').LastOrDefault(l => l.Contains("Passed!") || l.Contains("Failed!") ||
                                                           l.Contains("error CS") || l.Contains("No test matches"))?.Trim();
        var compiled = !text.Contains("error CS");
        return new { pass = p.ExitCode == 0, compiled, summary };
    }
}

/// <summary>
/// The R4 series (docs/17 6.2): N passes of the engine's reviewer on one diff,
/// with no prior findings, through the same ReviewerModel and claude-code
/// runtime as the in-run review. Classification is done afterwards by the
/// fixed rubric (tools/r4-analyse.py).
/// </summary>
public static class R4Series
{
    public static async Task<int> RunAsync(string[] args)
    {
        var series = Cli.Arg(args, "--series")!;
        var diffPath = Path.GetFullPath(Cli.Arg(args, "--diff")!);
        var spec = Cli.Arg(args, "--spec")!;
        var passes = int.Parse(Cli.Arg(args, "--passes") ?? "20");
        var parallel = int.Parse(Cli.Arg(args, "--parallel") ?? "4");
        var outPath = Path.GetFullPath(Cli.Arg(args, "--out")!);

        var specification = spec switch
        {
            "epic" => $"Epic: {Texts.Epic}",
            "A" => Texts.TicketA,
            "B" => Texts.TicketB,
            "C" => Texts.TicketC,
            _ => throw new ArgumentException(spec),
        };
        var diffText = File.ReadAllText(diffPath);
        var diffHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(diffText))).ToLowerInvariant()[..16];
        var scratch = Path.Combine(Cli.WorkDir, "r4-scratch");
        var reviewer = new RunContext($"r4-{series}", new Plan(), scratch, "main", "main", "R4",
            Path.Combine(scratch, "records"), OrganisationConfiguration.Load(Path.Combine(Cli.PlansDir, "organisation.yaml"))).Reviewer;

        var results = new List<object>();
        using var gate = new SemaphoreSlim(parallel);
        var tasks = Enumerable.Range(1, passes).Select(async i =>
        {
            await gate.WaitAsync();
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    var r = await reviewer.ReviewAsync(diffHash, diffText, Array.Empty<Finding>(), Array.Empty<string>(),
                        $"r4-{series}", CancellationToken.None, specification);
                    if (r.IsOk)
                    {
                        var v = r.Value!;
                        lock (results) results.Add(new
                        {
                            pass = i, ok = true, v.InputHash, v.Model, v.CostUsd, v.Outcome,
                            findings = v.Findings.Select(f => new { f.Rule, f.Path, f.Symbol, f.Snippet, f.Message, f.Blocking }),
                        });
                        Cli.Log($"R4 {series} pass {i}: {v.Findings.Count} finding(s)");
                        return;
                    }
                    Cli.Log($"R4 {series} pass {i} attempt {attempt}: {r.Error!.Class}/{r.Error.Reason} {r.Error.Detail}");
                    if (attempt >= 4)
                    {
                        lock (results) results.Add(new { pass = i, ok = false, error = $"{r.Error.Class}/{r.Error.Reason}: {r.Error.Detail}" });
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(30 * attempt));
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(new
        {
            series, spec, specification, diff = Path.GetFileName(diffPath), diffHash, passes,
            results = results.OrderBy(r => (int)r.GetType().GetProperty("pass")!.GetValue(r)!).ToList(),
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
