namespace FeatureFlow.Validation.Tests;

/// <summary>Locates <c>library/std</c> and makes mutated copies of it (LOG 70).</summary>
public static class Std
{
    public static string Root => Path.Combine(Repository.Root, "library", "std");

    public static string GoldenFolder => Path.Combine(Repository.Root, "tests", "FeatureFlow.Validation.Tests", "Golden");

    /// <summary>One text replacement in one std file. <see cref="Old"/> must occur exactly once.</summary>
    public sealed record Edit(string File, string Old, string New);

    /// <summary>Copies std to a temporary folder, applies the edits, and validates the copy as a library.</summary>
    public static ValidationResult Mutate(params Edit[] edits)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ff-std-" + Guid.NewGuid().ToString("N"));
        Copy(Root, dir);
        try
        {
            foreach (var edit in edits)
            {
                var path = Path.Combine(dir, edit.File);
                var text = File.ReadAllText(path);
                var count = Occurrences(text, edit.Old);
                if (count != 1)
                    throw new InvalidOperationException($"{edit.File}: the text to replace occurs {count} times, not once:\n{edit.Old}");
                File.WriteAllText(path, text.Replace(edit.Old, edit.New, StringComparison.Ordinal));
            }

            // The copy must be validated under std's own name; the folder name is not part of it.
            return LibraryValidator.ValidateLibrary(dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
            Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    public static string Describe(ValidationResult result) =>
        result.Diagnostics.Count == 0 ? "(no diagnostics)" : string.Join("\n", result.Diagnostics);
}
