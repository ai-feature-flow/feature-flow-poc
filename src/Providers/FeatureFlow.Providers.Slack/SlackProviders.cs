using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Slack;

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class SlackProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("notifications", "slack", ["token_ref", "signing_secret_ref", "users", "console_url", "api_base"], Create),
    ];

    /// <summary>
    /// The bot token and signing secret are secret references, resolved by the secrets provider when used, never values
    /// in configuration. <c>users</c> maps a Slack user id to the person it answers as:
    /// <c>{ U0123ABC: { actor: alice, roles: [approver], groups: [acme-leads] } }</c>.
    /// </summary>
    static SlackNotifications Create(ProviderContext c)
    {
        var tokenRef = c.RequiredSetting("token_ref");
        var signingRef = c.Setting("signing_secret_ref");
        ISecretsProvider Secrets() => c.Provider("secrets") as ISecretsProvider
                                      ?? throw new InvalidOperationException($"providers.notifications.instances.{c.Instance.Name} needs a secrets provider");
        return new SlackNotifications(
            ct => Secrets().ResolveAsync(tokenRef, ct),
            ct => signingRef is null
                ? Task.FromResult(ProviderResult<string>.Failure("unauthenticated", "no signing_secret_ref is configured: Slack's buttons cannot be verified"))
                : Secrets().ResolveAsync(signingRef, ct),
            Users(c),
            c.Setting("console_url"),
            apiBase: c.Setting("api_base") ?? "https://slack.com/api");
    }

    static Dictionary<string, ChatPerson> Users(ProviderContext c)
    {
        var users = new Dictionary<string, ChatPerson>(StringComparer.Ordinal);
        var where = $"providers.notifications.instances.{c.Instance.Name}.users";
        if (c.Instance.Settings["users"] is null or YScalar { IsNull: true })
            return users;
        if (c.Instance.Settings["users"] is not YMap map)
            throw new InvalidOperationException($"{where} maps a Slack user id to {{ actor, roles, groups }}");
        foreach (var (key, node) in map.Entries)
        {
            if (node is not YMap person || person["actor"].ScalarText() is not { Length: > 0 } actor)
                throw new InvalidOperationException($"{where}.{key.Text} needs an actor");
            users[key.Text] = new ChatPerson(actor, List(person["roles"]), person.Has("groups") ? List(person["groups"]) : null);
        }

        return users;

        static List<string> List(YNode? node) => node is YSeq seq ? seq.Items.Select(i => i.ScalarText() ?? "").Where(s => s.Length > 0).ToList() : [];
    }
}
