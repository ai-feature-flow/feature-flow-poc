namespace FeatureFlow.Api.Contracts;

/// <summary><c>GET /templates</c>: the entrypoints of the organisation's libraries, and the libraries that could not be read.</summary>
public sealed record TemplateList(IReadOnlyList<TemplateView> Templates, IReadOnlyList<UnreadableLibrary> Unreadable);

/// <summary>
/// A template a run may start: its reference (<c>std:templates/ticket_delivery</c>), library and name, the manifest's
/// <c>when</c>, its parameters, and the parameter whose value is the run's tracker issue when it names one
/// (<c>metadata.tracker_parameter</c>, LOG 501), so a start form need not ask for the tracker issue separately.
/// </summary>
public sealed record TemplateView(string Reference, string Library, string Name, string? When, IReadOnlyList<TemplateParameter> Parameters,
    string? TrackerParameter = null);

/// <summary>
/// A template's parameter: its name, type, whether it is required, its literal default as JSON text (the form
/// <c>StartRunRequest.parameters</c> takes), its default expression when the default is <c>{ expr: ... }</c>
/// (evaluated at run start, so <c>default</c> is null), and its description.
/// </summary>
public sealed record TemplateParameter(string Name, string Type, bool Required, string? Default, string? DefaultExpression, string? Description);

/// <summary>A library that could not be read, and why.</summary>
public sealed record UnreadableLibrary(string Library, string Error);
