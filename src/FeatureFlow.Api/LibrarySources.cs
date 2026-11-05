using System.Diagnostics;
using FeatureFlow.Model;

namespace FeatureFlow.Api;

/// <summary>
/// Where an organisation library's files come from (docs/08 section 3), resolved in one place for run start (the lock),
/// answers (decision types, read at the moment of use) and startup checks. A <c>directory</c> source is a folder,
/// relative to the organisation file. A <c>git</c> source is a repository and a branch or tag (its default branch when
/// none is named), fetched now: a shallow clone into a temporary folder, read into memory and deleted, so every read
/// sees the reference as it is and no clone is shared between processes. The clone uses the process's own Git
/// configuration and credentials, and never prompts.
/// </summary>
public static class LibrarySources
{
    static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The library the source names, or null and why not.</summary>
    public static async Task<(Library.Library? Library, string? Problem)> OpenAsync(LibrarySourceConfig source, string organisationLocation, CancellationToken ct)
    {
        if (source.Source != "git")
        {
            var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(organisationLocation))!, source.Path));
            return Directory.Exists(path) ? Open(path, $"the library at {path}") : (null, $"the directory {path} does not exist");
        }

        var checkout = Path.Combine(Path.GetTempPath(), $"ff-library-{Guid.NewGuid():N}");
        try
        {
            var where = source.Ref is null ? source.Repository : $"{source.Repository} at {source.Ref}";
            string[] branch = source.Ref is null ? [] : ["--branch", source.Ref];
            var (exit, error) = await GitAsync(["clone", "--quiet", "--depth", "1", "--no-tags", .. branch, "--", source.Repository!, checkout], ct);
            if (exit != 0)
                return (null, $"{where} does not clone: {error.Trim()}");
            var folder = Path.Combine(checkout, source.Path);
            return Directory.Exists(folder)
                ? Open(folder, source.Path.Length == 0 ? $"the library in {where}" : $"the library at {source.Path} in {where}")
                : (null, $"{where} has no folder {source.Path}");
        }
        finally
        {
            if (Directory.Exists(checkout))
                DeleteTree(checkout);
        }
    }

    static (Library.Library?, string?) Open(string path, string what)
    {
        var d = new DiagnosticBag();
        return Library.Library.Open(path, d) is { } library && !d.HasErrors
            ? (library, null)
            : (null, $"{what} does not open: {string.Join("; ", d.Where(x => x.Blocks).Take(3).Select(x => x.Message))}");
    }

    static async Task<(int Exit, string Error)> GitAsync(string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args)
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CloneTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (ct.IsCancellationRequested)
                throw;
            return (-1, $"git {args[0]} took longer than {CloneTimeout.TotalMinutes} minutes");
        }

        await output;
        return (process.ExitCode, await error);
    }

    /// <summary>Git writes its objects read-only, which Windows will not delete as they are.</summary>
    static void DeleteTree(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
