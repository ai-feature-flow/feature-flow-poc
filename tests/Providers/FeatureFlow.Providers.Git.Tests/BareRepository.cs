using System.Diagnostics;

namespace FeatureFlow.Providers.Git.Tests;

/// <summary>
/// A local bare repository standing in for the remote: what the Git provider reaches through its remote template.
/// Commits are written with plumbing, as someone other than the engine, so the provider's cache is never involved.
/// </summary>
public sealed class BareRepository
{
    public BareRepository(string root, string fullName)
    {
        Path = System.IO.Path.Combine(root, fullName + ".git");
        Directory.CreateDirectory(Path);
        Git("init", "-q", "--bare", "--initial-branch=main");
        Commit("refs/heads/main", new Dictionary<string, string?> { ["README.md"] = "# Fixture\n" });
    }

    public string Path { get; }

    /// <summary>The remote template that reaches repositories under <paramref name="root"/>.</summary>
    public static string Template(string root) => System.IO.Path.Combine(root, "{repo}.git");

    public string? Resolve(string reference)
    {
        var (exit, output) = Run(["rev-parse", "--verify", "-q", reference + "^{commit}"], null, null);
        return exit == 0 ? output.Trim() : null;
    }

    /// <summary>Commits <paramref name="changes"/> (null deletes) on <paramref name="reference"/>, or on <paramref name="from"/> when it does not exist yet.</summary>
    public string Commit(string reference, IReadOnlyDictionary<string, string?> changes, string? from = null, string message = "someone else's change")
    {
        var parent = Resolve(reference) ?? (from is null ? null : Resolve(from) ?? throw new InvalidOperationException($"{from} does not resolve"));
        var index = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ff-index-{Guid.NewGuid():N}");
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        try
        {
            if (parent is not null)
                Git(env, "read-tree", parent);
            foreach (var (path, content) in changes)
            {
                if (content is null)
                {
                    Git(env, "update-index", "--force-remove", path);
                    continue;
                }

                var blob = Run(["hash-object", "-w", "--stdin"], env, content).Output.Trim();
                Git(env, "update-index", "--add", "--cacheinfo", $"100644,{blob},{path}");
            }

            var tree = Git(env, "write-tree").Trim();
            var arguments = new List<string> { "commit-tree", tree, "-m", message };
            if (parent is not null)
                arguments.AddRange(["-p", parent]);
            var commit = Git(env, [.. arguments]).Trim();
            Git("update-ref", reference, commit);
            return commit;
        }
        finally
        {
            File.Delete(index);
        }
    }

    public string? Read(string revision, string path)
    {
        var (exit, output) = Run(["show", $"{revision}:{path}"], null, null);
        return exit == 0 ? output : null;
    }

    /// <summary>The first-parent history from <paramref name="from"/> (exclusive) to <paramref name="to"/>, oldest first.</summary>
    public IReadOnlyList<string> FirstParents(string from, string to) =>
        Git("rev-list", "--first-parent", "--reverse", $"{from}..{to}").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A commit's parents.</summary>
    public IReadOnlyList<string> Parents(string revision) => Git("rev-list", "--parents", "-n", "1", revision).Trim().Split(' ').Skip(1).ToList();

    public string Git(params string[] arguments) => Git(null, arguments);

    string Git(IReadOnlyDictionary<string, string>? env, params string[] arguments)
    {
        var (exit, output) = Run(arguments, env, null);
        return exit == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {output}");
    }

    (int Exit, string Output) Run(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? env, string? input)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false,
        };
        foreach (var a in arguments)
            start.ArgumentList.Add(a);
        start.Environment["GIT_DIR"] = Path;
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "Someone Else";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "someone@example.com";
        foreach (var (k, v) in env ?? new Dictionary<string, string>())
            start.Environment[k] = v;
        using var process = Process.Start(start)!;
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, process.ExitCode == 0 ? output.Result : error + output.Result);
    }
}
