using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>
/// The factories of this package, by type and kind, for a host to register (docs/06 section 4). The test doubles
/// (<c>memory</c>, <c>fake</c>, <c>scripted</c>, <c>recording</c>) are in <c>FeatureFlow.Providers.Testing</c>.
/// </summary>
public static class LocalProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("artifact_store", "filesystem", ["root"], c => new FileSystemArtifactStore(c.PathSetting("root") ?? c.RequiredSetting("root"))),
        new("record_store", "sqlite", ["path"], c => new SqliteRecordStore(c.PathSetting("path") ?? c.RequiredSetting("path"))),
        new("secrets", "file", ["path", "from_environment"], c => new FileSecrets(c.PathSetting("path") ?? c.RequiredSetting("path"),
            c.Instance.Settings["from_environment"] is YSeq names ? names.Items.Select(n => n.ScalarText() ?? "").ToList() : null)),
        // M7: version 1's session surface, a hand-off to the person's own agent tool (RW18).
        new("session_surface", "local", [], _ => new LocalSessionSurface()),
    ];
}
