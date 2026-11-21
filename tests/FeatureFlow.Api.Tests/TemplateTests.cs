using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;

namespace FeatureFlow.Api.Tests;

/// <summary><c>GET /templates</c> (G10): the organisation libraries' entrypoints and their parameters, for any authenticated caller.</summary>
public sealed class TemplateTests
{
    [Fact]
    public async Task The_libraries_entrypoints_are_listed_with_their_parameters_and_an_unreadable_library_is_named()
    {
        var ct = TestContext.Current.CancellationToken;
        var organisation = new OrganisationConfig("t",
            [
                new LibrarySourceConfig("std", "directory", Path.Combine(Repository.Root, "library", "std")),
                new LibrarySourceConfig("gone", "directory", Path.Combine(Repository.Root, "no-such-library")),
            ],
            new PolicyConfig([], null, null, null, null), "");
        await using var world = new ApiWorld(organisation);

        // Any authenticated caller, a viewer of nothing included (LOG 400); no credential is 401.
        using var response = await world.As("eve").GetAsync("templates", ct);
        using var anonymous = await new HttpClient { BaseAddress = world.Address }.GetAsync("templates", ct);
        var list = JsonSerializer.Deserialize<TemplateList>(await response.Content.ReadAsStringAsync(ct), ApiJson.Options)!;

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Unauthorized), (response.StatusCode, anonymous.StatusCode));
        Assert.Equal(["std:templates/ticket_delivery", "std:templates/epic_delivery", "std:templates/project_delivery"], list.Templates.Select(t => t.Reference));
        var ticket = list.Templates[0];
        Assert.Equal("One ticket, from plan to merge into its parent branch", ticket.When);
        Assert.Equal(new TemplateParameter("ticket_id", "string", true, null, null, "The tracker key, or the ticket's name in its epic's work list"),
            ticket.Parameters.Single(p => p.Name == "ticket_id"));
        Assert.Equal(("boolean", false, "false"), ticket.Parameters.Single(p => p.Name == "engine_can_merge") is var m ? (m.Type, m.Required, m.Default) : default);
        var unreadable = Assert.Single(list.Unreadable);
        Assert.Equal("gone", unreadable.Library);
        Assert.Contains("does not exist", unreadable.Error);
    }

    /// <summary>
    /// A literal default is JSON text, as <c>StartRunRequest.parameters</c> takes it; an <c>{ expr: ... }</c> default is
    /// evaluated at run start, so its <c>default</c> is null and the expression is given in <c>default_expression</c>.
    /// </summary>
    [Fact]
    public async Task A_literal_default_is_json_text_and_an_expression_default_is_given_as_its_expression()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "probe-template-defaults");
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        File.WriteAllText(Path.Combine(root, "library.yaml"), "name: probe\nversion: 0.1.0\nentrypoints:\n  - { template: defaults }\n");
        File.WriteAllText(Path.Combine(root, "templates", "defaults.yaml"), Defaults);
        var organisation = new OrganisationConfig("t", [new LibrarySourceConfig("probe", "directory", root)], new PolicyConfig([], null, null, null, null), "");
        await using var world = new ApiWorld(organisation);

        var json = await world.As("eve").GetStringAsync("templates", ct);
        var parameters = JsonSerializer.Deserialize<TemplateList>(json, ApiJson.Options)!.Templates.Single().Parameters.ToDictionary(p => p.Name);

        Assert.Equal(("\"main\"", null), (parameters["branch"].Default, parameters["branch"].DefaultExpression));
        Assert.Equal(("[\"a\",\"b\"]", null), (parameters["labels"].Default, parameters["labels"].DefaultExpression));
        Assert.Equal(("3", null), (parameters["attempts"].Default, parameters["attempts"].DefaultExpression));
        Assert.Equal((null, "run.repositories[0]"), (parameters["repository"].Default, parameters["repository"].DefaultExpression));
        Assert.Equal("main", JsonSerializer.Deserialize<string>(parameters["branch"].Default!));
    }

    const string Defaults = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: defaults, level: ticket }
        parameters:
          branch: { type: string, default: main }
          labels: { type: list, default: [a, b] }
          attempts: { type: integer, default: 3 }
          repository: { type: string, default: { expr: "run.repositories[0]" } }
        steps:
          - id: comment
            action: tracker.update
            with: { op: comment, issue: T-1, body: started }
            routes:
              done: { end: completed }
        """;
}
