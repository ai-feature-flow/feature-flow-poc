using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeatureFlow.Api.Contracts;

/// <summary>Every refusal and failure the API answers: one message (docs/02 section 7).</summary>
public sealed record ErrorResponse(string Error);

/// <summary>
/// The API's JSON (docs/02 section 7): snake_case property names and enum values, required constructor parameters and
/// nullable annotations respected. The API, its OpenAPI document and the CLI use these options and no others (LOG A3-0a).
/// </summary>
public static class ApiJson
{
    /// <summary>The options responses are written and read with.</summary>
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>The options request bodies are read with: as <see cref="Options"/>, and a property the record does not name is refused.</summary>
    public static readonly JsonSerializerOptions Requests = new(Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Sets the API's naming and checking on <paramref name="options"/>, such as ASP.NET Core's own, and returns them.</summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.DictionaryKeyPolicy = null;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        options.WriteIndented = false;
        if (!options.Converters.Any(c => c is JsonStringEnumConverter))
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}
