namespace FeatureFlow.Model;

/// <summary>
/// The run-time override catalogue (docs/05 section 5): every key, its default people roles, and its variants for
/// protected content (<c>_protected</c>) or beyond the hard attempt ceiling (<c>grant_attempts_beyond</c>). The one place
/// both the API (who may) and the workflow (which variant the caller holds) read. The organisation may change the roles for
/// any key, a variant included, in <c>policy.overrides</c> (docs/07 section 3).
/// </summary>
public static class Overrides
{
    public const string Answer = "answer";
    public const string Recover = "recover";
    public const string Breakpoint = "breakpoint";
    public const string Skip = "skip";
    public const string Amend = "amend";
    public const string ChangeRole = "change_role";
    public const string GrantAttempts = "grant_attempts";
    public const string OverrideLibrary = "override_library";
    public const string AnswerQuestion = "answer_question";
    public const string Abort = "abort";

    public const string SkipProtected = "skip_protected";
    public const string AmendProtected = "amend_protected";
    public const string OverrideLibraryProtected = "override_library_protected";
    public const string GrantAttemptsBeyond = "grant_attempts_beyond";

    const string Steerer = "steerer";
    const string Admin = "admin";

    /// <summary>
    /// Every key with its default roles. <see cref="Answer"/> and <see cref="AnswerQuestion"/> have none here: approval
    /// rules decide who may answer (docs/05 section 3.2).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultRoles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [Answer] = [],
        [AnswerQuestion] = [],
        [Recover] = [Steerer],
        [Breakpoint] = [Steerer],
        [Skip] = [Steerer],
        [SkipProtected] = [Admin],
        [Amend] = [Steerer],
        [AmendProtected] = [Admin],
        [ChangeRole] = [Admin],
        [GrantAttempts] = [Steerer],
        [GrantAttemptsBeyond] = [Admin],
        [OverrideLibrary] = [Steerer],
        [OverrideLibraryProtected] = [Admin],
        [Abort] = [Steerer],
    };

    /// <summary>The variants of a key, by the base key: what a caller must also hold to use the override on protected content or beyond the hard ceiling.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Variants = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [Skip] = [SkipProtected],
        [Amend] = [AmendProtected],
        [OverrideLibrary] = [OverrideLibraryProtected],
        [GrantAttempts] = [GrantAttemptsBeyond],
    };

    public static bool IsKey(string key) => DefaultRoles.ContainsKey(key);

    /// <summary>
    /// The roles that may use <paramref name="key"/>: the organisation's <c>policy.overrides</c> entry, else the default.
    /// Where a steerer may, an administrator may too (docs/05 section 3.3).
    /// </summary>
    public static IReadOnlyList<string> RolesFor(string key, IReadOnlyDictionary<string, IReadOnlyList<string>> organisation)
    {
        var roles = organisation.TryGetValue(key, out var configured) ? configured
            : DefaultRoles.TryGetValue(key, out var standard) ? standard
            : throw new ArgumentException($"'{key}' is not a run-time override key");
        return roles.Contains(Steerer) && !roles.Contains(Admin) ? [.. roles, Admin] : roles;
    }
}
