using System.Text.Json;
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
            foreach (var (source, library, problem) in await libraries.ReadAsync(services.Organisation, context.RequestAborted))
            {
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
    }

    /// <summary>An entrypoint with its template's declared parameters.</summary>
    static TemplateView TemplateOf(Library.Library library, Entrypoint entrypoint)
    {
        var reference = new LibraryReference(library.Name, null, "templates", entrypoint.Template);
        var d = new DiagnosticBag();
        var parameters = library.Yaml("templates", entrypoint.Template) is { } root
                         && DocumentReader.Read(root, "template", entrypoint.Template, library.Relative(library.PathOf("templates", entrypoint.Template)!), reference, d) is { } read
            ? read.Info.Parameters
            : [];
        return new TemplateView(reference.ToString(), library.Name, entrypoint.Template, entrypoint.When,
            parameters.Select(ParameterOf).ToList());
    }

    /// <summary>A literal default as JSON text; an <c>{ expr: ... }</c> default is evaluated at run start, so it is given as its expression.</summary>
    static TemplateParameter ParameterOf(ParameterDecl p) => p.Default switch
    {
        null => new(p.Name, p.Type, p.Required, null, null, p.Description),
        var d when d.IsSingleKeyMap("expr", out var e) => new(p.Name, p.Type, p.Required, null, e.ScalarText(), p.Description),
        var d => new(p.Name, p.Type, p.Required, JsonSerializer.Serialize(d.ToPlain(), Json), null, p.Description),
    };
}
