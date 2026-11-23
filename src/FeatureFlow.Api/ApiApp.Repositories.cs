using FeatureFlow.Api.Contracts;
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

    sealed partial class Handlers
    {
        /// <summary>The repositories registered and named by runs the caller views: not built yet.</summary>
        public Task<IResult> ListRepositoriesAsync(HttpContext context, Caller caller) => Task.FromResult(Error(501, "not built yet (B1 A16)"));

        /// <summary>Registers a repository: not built yet.</summary>
        public Task<IResult> RegisterRepositoryAsync(HttpContext context, Caller caller) => Task.FromResult(Error(501, "not built yet (B1 A16)"));
    }
}
