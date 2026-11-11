using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter.ModelTargets;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Builds the Arbiter model targets table a Squad render writes beside each hooked harness's hooks:
/// the token each profile resolves to for that harness, and the profile and token each agent runs on.
/// </summary>
/// <remarks>
/// The table is the arbiter's read of what a model preset actually routes to, so it is produced from
/// <see cref="SquadModelResolution.ResolveToken(SquadModelProfiles, string, string)"/> and nothing else:
/// a second resolution path would let the table drift from the agent files it describes. Project scope
/// writes it under <c>artifacts/arbiter</c>; global scope writes it under the user's <c>.kyber-weave</c>
/// directory; instance scope writes none, because no hook runs there.
/// </remarks>
public static class ArbiterModelTargetsFile
{
    /// <summary>The preset name a render with no model preset records.</summary>
    public const string BundledPresetName = "bundled";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>The project-scope path of one harness's table, relative to the project root.</summary>
    /// <param name="harness">The harness token.</param>
    public static string ProjectRelativePath(string harness) => $"artifacts/arbiter/model-targets.{harness}.json";

    /// <summary>The global-scope path of one harness's table, relative to the user's home.</summary>
    /// <param name="harness">The harness token.</param>
    public static string GlobalRelativePath(string harness) => $".kyber-weave/arbiter/model-targets.{harness}.json";

    /// <summary>
    /// Builds one table file per hooked target in the request, when the Arbiter is enabled and the scope
    /// is not instance. Each target's profiles are its preset-merged profiles, so the table reflects the
    /// tokens the render just wrote.
    /// </summary>
    /// <param name="request">The render request; its <see cref="SquadRenderRequest.PreloadedSource"/> must be set.</param>
    /// <returns>The table files, one per hooked target, or none when the Arbiter is off.</returns>
    public static IReadOnlyList<SquadDeploymentFile> BuildFiles(SquadRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Arbiter is not { Enabled: true } || request.Scope == SquadDeploymentScope.Instance)
        {
            return [];
        }

        if (request.PreloadedSource is null)
        {
            throw new InvalidOperationException("A model targets table needs the loaded source it was rendered from.");
        }

        SquadSource bundled = request.PreloadedSource;
        List<SquadDeploymentFile> files = [];
        foreach (SquadTarget target in request.Targets)
        {
            string harness = SquadTargetCatalog.GetToken(target);
            if (!ArbiterHookWiring.HookedTargets.Contains(harness))
            {
                continue;
            }

            SquadSource merged = SquadRenderSource.Load(request, target);
            SquadModelPreset? preset = request.ModelPresets is not null
                && request.ModelPresets.TryGetValue(target, out SquadModelPreset? found)
                    ? found
                    : null;
            Dictionary<string, string> agentProfiles = new(StringComparer.Ordinal);
            foreach (SquadAgent agent in merged.Agents)
            {
                agentProfiles[agent.Name] = agent.ModelProfile;
            }

            byte[] content = Serialize(
                harness,
                preset?.Name ?? BundledPresetName,
                preset?.Digest ?? string.Empty,
                BundleDigest(bundled.ModelProfiles, harness),
                merged.ModelProfiles,
                agentProfiles);
            string path = request.Scope == SquadDeploymentScope.Global
                ? GlobalRelativePath(harness)
                : ProjectRelativePath(harness);
            files.Add(new SquadDeploymentFile(path, content, harness));
        }

        return files;
    }

    /// <summary>Serializes one harness's table to UTF-8 JSON, profiles and agents in ordinal name order.</summary>
    /// <param name="harness">The harness token the table is for.</param>
    /// <param name="presetName">The preset name, or <see cref="BundledPresetName"/>.</param>
    /// <param name="presetDigest">The preset content digest, or empty when bundled.</param>
    /// <param name="bundleDigest">The digest of the bundled profiles the table was resolved from.</param>
    /// <param name="profiles">The effective (preset-merged) model profiles.</param>
    /// <param name="agentProfiles">Each canonical agent name to its profile category.</param>
    public static byte[] Serialize(
        string harness,
        string presetName,
        string presetDigest,
        string bundleDigest,
        SquadModelProfiles profiles,
        IReadOnlyDictionary<string, string> agentProfiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(agentProfiles);

        Dictionary<string, string?> profileTokens = new(StringComparer.Ordinal);
        foreach (string profile in profiles.Profiles.Keys.Order(StringComparer.Ordinal))
        {
            profileTokens[profile] = SquadModelResolution.ResolveToken(profiles, profile, harness);
        }

        Dictionary<string, ArbiterModelTargetsAgent> agents = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> agent in agentProfiles.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            agents[agent.Key] = new ArbiterModelTargetsAgent(
                agent.Value,
                SquadModelResolution.ResolveToken(profiles, agent.Value, harness));
        }

        ArbiterModelTargets table = new(
            ArbiterModelTargets.SchemaName,
            harness,
            new ArbiterModelTargetsPreset(presetName, presetDigest),
            bundleDigest,
            profileTokens,
            agents);
        return JsonSerializer.SerializeToUtf8Bytes(table, WriteOptions);
    }

    /// <summary>
    /// The digest of the bundled profiles as one harness resolves them. It names the bundle a table was
    /// written against, so a table from an older bundle is recognisable.
    /// </summary>
    /// <param name="bundled">The bundled profiles, before any preset.</param>
    /// <param name="harness">The harness token.</param>
    public static string BundleDigest(SquadModelProfiles bundled, string harness)
    {
        ArgumentNullException.ThrowIfNull(bundled);
        StringBuilder lines = new();
        foreach (string profile in bundled.Profiles.Keys.Order(StringComparer.Ordinal))
        {
            lines.Append(profile).Append('=').Append(SquadModelResolution.ResolveToken(bundled, profile, harness) ?? "inherit").Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(harness + "\n" + lines)));
    }
}
