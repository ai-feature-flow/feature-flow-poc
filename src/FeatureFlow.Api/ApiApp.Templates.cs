using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>Templates (G10): the organisation libraries' entrypoints, with the parameters a start form asks for.</summary>
    static void MapTemplates(WebApplication app, Handlers api)
    {
        app.MapGet("/templates", (Delegate)((HttpContext c) => api.Guard(c, _ => api.TemplatesAsync(c))))
            .Described<TemplateList>("listTemplates", 200);
    }

    sealed partial class Handlers
    {
        /// <summary>
        /// The entrypoints of each organisation library (docs/08 section 2), for any authenticated caller (LOG A3-0m): listing
        /// them shows no run's data, and starting a run is still authorised in its scope. A library that cannot be read is
        /// listed with why, not a failure of the route.
        /// </summary>
        public async Task<IResult> TemplatesAsync(HttpContext context)
        {
            var templates = new List<TemplateView>();
            var unreadable = new List<UnreadableLibrary>();
            foreach (var source in services.Organisation.Libraries)
            {
                var (library, problem) = await OpenLibraryAsync(source, context.RequestAborted);
                if (library is null)
                {
                    unreadable.Add(new UnreadableLibrary(source.Name, problem!));
                    continue;
                }

                foreach (var entrypoint in library.Manifest.Entrypoints)
                    templates.Add(TemplateOf(library, entrypoint));
            }

            return Results.Json(new TemplateList(templates, unreadable), Json);
        }

        /// <summary>
        /// An organisation library as it is now. The one place a read route opens a library, so a per-process cache
        /// (LOG A3-0h) can wrap it.
        /// </summary>
        Task<(Library.Library? Library, string? Problem)> OpenLibraryAsync(LibrarySourceConfig source, CancellationToken ct) =>
            LibrarySources.OpenAsync(source, services.Organisation.Location, ct);
    }

    /// <summary>An entrypoint with its template's declared parameters, each marked when the library's <c>inputs</c> supply it.</summary>
    static TemplateView TemplateOf(Library.Library library, Entrypoint entrypoint)
    {
        var reference = new LibraryReference(library.Name, null, "templates", entrypoint.Template);
        var d = new DiagnosticBag();
        var parameters = library.Yaml("templates", entrypoint.Template) is { } root
                         && DocumentReader.Read(root, "template", entrypoint.Template, library.Relative(library.PathOf("templates", entrypoint.Template)!), reference, d) is { } read
            ? read.Info.Parameters
            : [];
        return new TemplateView(reference.ToString(), library.Name, entrypoint.Template, entrypoint.When,
            parameters.Select(p => new TemplateParameter(p.Name, p.Type, p.Required, p.Default is null ? null : YamlWriter.Write(p.Default), p.Description,
                library.Manifest.Inputs?.Has(p.Name) == true)).ToList());
    }
}
