using System.Diagnostics;
using System.Text.Json;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;
using Temporalio.Worker;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// Review 12 A6: replay tests replayed histories fetched live from the dev
/// server, against the code that produced them. They were not kept, so they
/// could not run in CI after a server reset, and the replayed runs were the
/// linear ones — the replay bug section 10 found was in the failure path.
///
/// This captures histories as committed fixtures and replays all of them. These
/// become the engine's first replay suite (S8).
/// </summary>
public static class FixtureCapture
{
    private const string Address = "localhost:7233";

    /// <summary>Where fixtures live. Committed, so CI can replay them.</summary>
    private static string FixtureDir()
    {
        var dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "histories"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Capture the histories of the named runs as fixtures.</summary>
    public static async Task<int> CaptureAsync(string[] args)
    {
        var names = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--id")
                names.Add(args[i + 1]);
        }

        if (names.Count == 0)
        {
            Console.WriteLine("FIXTURE usage: fixture capture --id <workflow-id> [--id <another>]");
            return 1;
        }

        var dir = FixtureDir();
        foreach (var id in names)
        {
            try
            {
                var json = await FetchHistoryAsync(id);
                var path = Path.Combine(dir, $"{id}.json");
                await File.WriteAllTextAsync(path, json);
                Console.WriteLine($"FIXTURE captured {id} -> {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FIXTURE failed {id}: {ex.Message}");
            }
        }
        return 0;
    }

    /// <summary>Replay every committed fixture.</summary>
    public static async Task<int> ReplayAllAsync()
    {
        var dir = FixtureDir();
        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();

        if (files.Count == 0)
        {
            Console.WriteLine($"FIXTURE no fixtures in {dir}");
            return 1;
        }

        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions()
            .AddWorkflow<InterpreterWorkflow>());

        var failures = 0;
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var history = Temporalio.Common.WorkflowHistory.FromJson(name, json);
                await replayer.ReplayWorkflowAsync(history);
                Console.WriteLine($"FIXTURE replay {name}: PASS ({history.Events.Count} events)");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FIXTURE replay {name}: FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine(failures == 0
            ? $"FIXTURE all {files.Count} fixtures replayed cleanly"
            : $"FIXTURE {failures} of {files.Count} failed");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<string> FetchHistoryAsync(string id)
    {
        var cli = Environment.GetEnvironmentVariable("TEMPORAL_CLI")
                  ?? @"C:\tools\temporal\temporal.exe";
        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("workflow");
        psi.ArgumentList.Add("show");
        psi.ArgumentList.Add("--workflow-id");
        psi.ArgumentList.Add(id);
        psi.ArgumentList.Add("--address");
        psi.ArgumentList.Add(Address);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add("json");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start temporal CLI");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"temporal CLI failed: {stderr}");

        // Normalise so the fixture is stable and diffable.
        using var doc = JsonDocument.Parse(stdout);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
    }
}
