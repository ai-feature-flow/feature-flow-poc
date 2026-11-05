using System.Diagnostics;
using System.Text.Json;
using FeatureFlow.Library;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// A run from an organisation library whose source is <c>git</c> (docs/08 section 3): the API fetches the branch at run
/// start and locks what it read, so a later push changes the next run's lock and not the first's (docs/08 section 5.1).
/// </summary>
[Trait("Category", "Integration")]
public sealed class GitLibraryRunTests : IDisposable
{
    const string Task = "tasks/build.md";

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-it-gitlib-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task A_run_locks_the_library_as_its_branch_was_at_run_start()
    {
        var work = Path.Combine(folder, "work");
        var bare = Path.Combine(folder, "m7steer.git");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Harness.TestLibraries, "m7steer"), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(work, Path.GetRelativePath(Path.Combine(Harness.TestLibraries, "m7steer"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        Git(work, "init", "-q", "--initial-branch=main");
        Git(work, "add", "-A");
        Git(work, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "m7steer");
        Git(folder, "clone", "-q", "--bare", work, bare);
        await using var h = await Harness.StartAsync("gitlib", librarySources: [$"{{ name: m7steer, source: git, repository: \"{bare}\", ref: main }}"]);

        var first = h.Start("m7steer:templates/steer_gate");
        Assert.NotNull(await h.WaitForWaitAsync(first, TimeSpan.FromMinutes(2)));
        File.AppendAllText(Path.Combine(work, Task), "\nPushed after the first run started.\n");
        Git(work, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-am", "change");
        Git(work, "push", "-q", bare, "main");
        var second = h.Start("m7steer:templates/steer_gate");
        Assert.NotNull(await h.WaitForWaitAsync(second, TimeSpan.FromMinutes(2)));

        var (firstTask, secondTask) = (await LockedTextAsync(h, first), await LockedTextAsync(h, second));
        Assert.Equal(File.ReadAllText(Path.Combine(Harness.TestLibraries, "m7steer", Task)), firstTask);
        Assert.EndsWith("Pushed after the first run started.\n", secondTask);
    }

    /// <summary>The task file's text as the run's lock pinned it.</summary>
    static async Task<string> LockedTextAsync(Harness h, string runId)
    {
        var ct = TestContext.Current.CancellationToken;
        var records = h.Host.Providers.Required<IRecordStoreProvider>("record_store");
        var store = h.Host.Providers.Required<IArtifactStoreProvider>("artifact_store");
        var lockRef = (await records.QueryAsync<LockRecord>(Queries.Lock, new Dictionary<string, string> { ["run_id"] = runId }, ct)).Unwrap().Single().LockRef;
        var locked = JsonSerializer.Deserialize<LibraryLock>(await TextAsync(lockRef), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return await TextAsync(locked.Libraries.Single(l => l.Name == "m7steer").Files[Task]);

        async Task<string> TextAsync(string reference)
        {
            await using var stream = (await store.GetAsync(new ArtifactRef(reference), ct)).Unwrap();
            return await new StreamReader(stream).ReadToEndAsync(ct);
        }
    }

    static void Git(string cwd, params string[] args)
    {
        Directory.CreateDirectory(cwd);
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }
}
