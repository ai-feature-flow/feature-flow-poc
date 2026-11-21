namespace FeatureFlow.Api.Contracts;

/// <summary><c>GET /templates</c>: the entrypoints of the organisation's libraries, and the libraries that could not be read.</summary>
public sealed record TemplateList(IReadOnlyList<TemplateView> Templates, IReadOnlyList<UnreadableLibrary> Unreadable);

/// <summary>
/// A template a run may start: its reference (<c>std:templates/ticket_delivery</c>), library and name, the manifest's
/// <c>when</c>, and its parameters.
/// </summary>
public sealed record TemplateView(string Reference, string Library, string Name, string? When, IReadOnlyList<TemplateParameter> Parameters);

/// <summary>
/// A template's parameter: its name, type, whether it is required, its default as JSON text, its description, and whether
/// the library's <c>inputs</c> supply it, so a form need not ask for it.
/// </summary>
public sealed record TemplateParameter(string Name, string Type, bool Required, string? Default, string? Description, bool FromLibrary);

/// <summary>A library that could not be read, and why.</summary>
public sealed record UnreadableLibrary(string Library, string Error);
