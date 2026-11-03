using System.Text.Json;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Providers.Abstractions;

/// <summary>
/// How artifact records are written and read. Library artifact types (a findings report, a triage) have snake_case keys
/// (RW12) and may be YAML or JSON, since JSON is YAML; the engine's own shapes (evidence, sets) are JSON with the
/// engine's camelCase keys, as M5's evidence already is.
/// </summary>
public static class ArtifactShapes
{
    /// <summary>The engine's shapes: evidence (<see cref="TestEvidence"/> and the rest) and <see cref="ArtifactSet"/>.</summary>
    public static readonly JsonSerializerOptions Engine = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Library shapes: <see cref="FindingsReport"/>, <see cref="Triage"/>.</summary>
    public static readonly JsonSerializerOptions Library = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes a library shape as JSON (which is also YAML).</summary>
    public static string WriteLibrary<T>(T value) => JsonSerializer.Serialize(value, Library);

    /// <summary>
    /// Reads a library shape from YAML or JSON text. Throws <see cref="FormatException"/> when the text does not parse or
    /// does not fit <typeparamref name="T"/>; checking it against its artifact type's schema is the caller's.
    /// </summary>
    public static T ReadLibrary<T>(string text, string what)
    {
        var d = new DiagnosticBag();
        var node = YamlText.Parse(text, what, d);
        if (node is null || d.HasErrors)
            throw new FormatException($"{what} does not parse: {string.Join("; ", d)}");
        try
        {
            return JsonSerializer.Deserialize<T>(YamlWriter.Write(node), Library) ?? throw new FormatException($"{what} is empty");
        }
        catch (JsonException e)
        {
            throw new FormatException($"{what} is not a {typeof(T).Name}: {e.Message}", e);
        }
    }
}

/// <summary>
/// An artifact set (docs/01 section 5.1), made by <c>artifact.compose</c>: named members at exact references, judged as
/// one subject by a validator, reviewer or person. Stored as an engine-shape JSON artifact; it copies nothing.
/// </summary>
public sealed record ArtifactSet(string Name, IReadOnlyList<ArtifactSetMember> Members);

/// <summary>One member of an <see cref="ArtifactSet"/>: the artifact's name in its run, its declared type, and its reference.</summary>
public sealed record ArtifactSetMember(string Name, string? Type, string Ref);
