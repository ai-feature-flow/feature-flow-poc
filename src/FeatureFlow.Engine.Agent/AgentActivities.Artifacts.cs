using System.Text.Json;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Exceptions;

namespace FeatureFlow.Engine.Agent;

public sealed partial class AgentActivities
{
    sealed partial class Run
    {
        readonly Dictionary<string, ArtifactTypeItem?> artifactTypes = new(StringComparer.Ordinal);

        /// <summary>
        /// Verify (docs/04 section 3.1, step 5; docs/01 section 5.4; LOG 246): copies each artifact of an accepted report out of
        /// the sandbox, once, and checks the stored bytes against the declared type: it reads in its format, fits its schema
        /// or Markdown structure, and passes the type's own validators through the validator providers. The engine reads what
        /// it stored, never what the agent says about it, so an agent cannot pass by declaring its artifact valid. The
        /// references are what the step keeps; nothing after this reads the output folder again.
        /// </summary>
        async Task<(IReadOnlyDictionary<string, string> Stored, IReadOnlyList<string> Errors)> CheckArtifactsAsync(StepReport report)
        {
            var stored = new Dictionary<string, string>(StringComparer.Ordinal);
            var errors = new List<string>();
            var success = call.Contract.Outcomes.Any(o => o.Name == report.Outcome && o.Class == "success");
            foreach (var (name, path) in report.Artifacts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var reference = Must(await sandboxes.CopyOutAsync(sandbox, $"{paths.Output}/{path}", ct), $"copy out artifact {name}").Id;
                stored[name] = reference;
                var declared = call.Contract.Artifacts.FirstOrDefault(a => a.Name == name && (a.Outcome == report.Outcome || (a.Outcome is null && success)))?.Type;
                if (declared is null)
                    continue;
                var type = await ArtifactTypeAsync(declared)
                           ?? throw new StepCannotAnswerException($"artifact {name}'s type {declared} is not in the run's libraries, so it cannot be checked");
                try
                {
                    errors.AddRange(await ArtifactTypeCheck.CheckAsync(type, name, declared, path, await TextAsync(reference), reference, owner.Providers, ct));
                }
                catch (ArtifactCheckException e) when (e.Infrastructure)
                {
                    throw new ApplicationFailureException(e.Message, errorType: "infrastructure");
                }
                catch (ArtifactCheckException e)
                {
                    throw new StepCannotAnswerException(e.Message);
                }
            }

            if (errors.Count > 0)
                await EventAsync("artifacts_invalid", string.Join("\n", errors));
            return (stored, errors);
        }

        /// <summary>The artifact type a reference names, read from the run's library lock (docs/08 section 5): the run's version of it.</summary>
        async Task<ArtifactTypeItem?> ArtifactTypeAsync(string declared)
        {
            if (artifactTypes.TryGetValue(declared, out var cached))
                return cached;
            var lockDocument = JsonSerializer.Deserialize<LibraryLock>(await TextAsync(spec.LockRef), Json)!;
            return artifactTypes[declared] = await lockDocument.ArtifactTypeAsync(declared, TextAsync);
        }
    }
}
