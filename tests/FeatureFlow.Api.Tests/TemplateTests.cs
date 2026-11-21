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

        // Any authenticated caller, a viewer of nothing included (LOG A3-0m); no credential is 401.
        using var response = await world.As("eve").GetAsync("templates", ct);
        using var anonymous = await new HttpClient { BaseAddress = world.Address }.GetAsync("templates", ct);
        var list = JsonSerializer.Deserialize<TemplateList>(await response.Content.ReadAsStringAsync(ct), ApiJson.Options)!;

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.Unauthorized), (response.StatusCode, anonymous.StatusCode));
        Assert.Equal(["std:templates/ticket_delivery", "std:templates/epic_delivery", "std:templates/project_delivery"], list.Templates.Select(t => t.Reference));
        var ticket = list.Templates[0];
        Assert.Equal("One ticket, from plan to merge into its parent branch", ticket.When);
        Assert.Equal(new TemplateParameter("ticket_id", "string", true, null, "The tracker key, or the ticket's name in its epic's work list", false),
            ticket.Parameters.Single(p => p.Name == "ticket_id"));
        Assert.Equal(("boolean", false, "false"), ticket.Parameters.Single(p => p.Name == "engine_can_merge") is var m ? (m.Type, m.Required, m.Default) : default);
        var unreadable = Assert.Single(list.Unreadable);
        Assert.Equal("gone", unreadable.Library);
        Assert.Contains("does not exist", unreadable.Error);
    }
}
