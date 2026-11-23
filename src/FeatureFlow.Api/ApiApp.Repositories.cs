using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>Repositories (A16): those registered and those runs name; registering one.</summary>
    static void MapRepositories(WebApplication app, Handlers api)
    {
        app.MapGet("/repositories", (Delegate)((HttpContext c) => api.Guard(c, caller => api.ListRepositoriesAsync(c, caller))))
            .Described<RepositoryList>("listRepositories", 200);
        app.MapPost("/repositories", (Delegate)((HttpContext c) => api.Guard(c, caller => api.RegisterRepositoryAsync(c, caller))))
            .Taking<RegisterRepositoryRequest>().Described<RepositoryView>("registerRepository", 201, 400, 409);
    }

    /// <summary>The host of every repository registered in B1: GitHub, by name or URL (a local Git path is E5, later).</summary>
    const string GitHubHost = "github";

    /// <summary>
    /// <c>owner/name</c> from <c>owner/name</c> or <c>https://github.com/owner/name</c> (with <c>.git</c> or a trailing
    /// slash), else null: an owner is letters, digits and inner hyphens, a name letters, digits, <c>.</c>, <c>_</c> and <c>-</c>.
    /// </summary>
    static string? GitHubNameOf(string text)
    {
        var name = text.Trim();
        const string site = "https://github.com/";
        if (name.StartsWith(site, StringComparison.OrdinalIgnoreCase))
        {
            name = name[site.Length..].TrimEnd('/');
            if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
        }

        var parts = name.Split('/');
        static bool Owner(string s) => s.Length is > 0 and <= 39 && !s.StartsWith('-') && !s.EndsWith('-') && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
        static bool Repository(string s) => s.Length is > 0 and <= 100 && s is not ("." or "..") && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
        return parts.Length == 2 && Owner(parts[0]) && Repository(parts[1]) ? name : null;
    }

    static RepositoryView RepositoryViewOf(RepositoryRecord r) => new(r.Name, r.Host, r.Url, r.DefaultBranch, true, r.RegisteredBy, r.At);

    sealed partial class Handlers
    {
        /// <summary>
        /// The repositories registered, and those named by runs the caller views that are not, marked unregistered; by name.
        /// Registration is not a secret: every signed-in caller sees every registered repository.
        /// </summary>
        public async Task<IResult> ListRepositoriesAsync(HttpContext context, Caller caller)
        {
            var ct = context.RequestAborted;
            var registered = await RegisteredAsync(ct);
            var runs = (await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string>(), ct)).Unwrap()
                .Where(r => RolesIn(caller, ScopeOf(r.Repositories)).Contains(PeopleRoles.Viewer));
            var named = runs.SelectMany(r => r.Repositories).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => !registered.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                .Select(name => new RepositoryView(name, GitHubHost, GitHubNameOf(name) is { } n ? $"https://github.com/{n}" : null, null, false, null, null));
            var all = registered.Select(RepositoryViewOf).Concat(named).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.Ordinal);
            return Results.Json(new RepositoryList(all.ToList()), Json);
        }

        /// <summary>
        /// Registers a GitHub repository by <c>owner/name</c> or URL, once the code host has found it and its default branch.
        /// Anyone signed in may register (product owner, 24 Nov: a benign action); a local path is refused until E5.
        /// </summary>
        public async Task<IResult> RegisterRepositoryAsync(HttpContext context, Caller caller)
        {
            var (body, malformed) = await BodyAsync<RegisterRepositoryRequest>(context);
            if (body is null)
                return malformed!;
            if (body.Path is not null)
                return Error(400, "registering a local Git repository by path is not supported yet (E5); register a GitHub repository by owner/name or URL");
            if (body.Github is null || GitHubNameOf(body.Github) is not { } name)
                return Error(400, "github must name a GitHub repository, as owner/name or https://github.com/owner/name");
            var ct = context.RequestAborted;
            if ((await RegisteredAsync(ct)).FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) is { } existing)
                return Error(409, $"{existing.Name} is already registered, by {existing.RegisteredBy}");
            if (services.Providers.ForRun("code_host", false) is not ICodeHostProvider host)
                return Error(400, $"{name} cannot be checked: no code host is configured");
            var branch = await host.GetDefaultBranchAsync(new RepoRef(name), ct);
            if (branch.Error is { } error)
                return Error(400, error.Reason == "not_found"
                    ? $"{name} was not found on GitHub, or the engine's GitHub App cannot see it"
                    : $"{name} cannot be checked with GitHub now ({error.Reason}: {error.Message})");
            var record = new RepositoryRecord(name, GitHubHost, $"https://github.com/{name}", branch.Value, caller.Actor, DateTimeOffset.UtcNow);
            (await Records.AppendAsync(new RecordBatch(Repositories: [record]), ct)).Unwrap();
            return Results.Json(RepositoryViewOf(record), Json, statusCode: 201);
        }

        async Task<IReadOnlyList<RepositoryRecord>> RegisteredAsync(CancellationToken ct) =>
            (await Records.QueryAsync<RepositoryRecord>(Queries.Repositories, new Dictionary<string, string>(), ct)).Unwrap();
    }
}
