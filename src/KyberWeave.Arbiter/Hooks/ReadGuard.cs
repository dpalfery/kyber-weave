using System.Text.Json;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The Req 25 enforcement point (design section 12): the planning-path Read guard and
/// the implementation-specialist test behind header stripping.
/// </summary>
/// <remarks>
/// A guarded caller is an agent in a <c>worker</c> or <c>publishing-worker</c> capability
/// profile — the same set the Squad wiring guards. docs-dev carries the
/// <c>documentation</c> profile, so Req 25.3 (docs-dev keeps unguarded document access)
/// holds structurally: there is deliberately no name-based carve-out. Shell commands
/// match by substring, which is best effort because indirection escapes it (R24).
/// </remarks>
public static class ReadGuard
{
    private static readonly IReadOnlySet<string> GuardedProfiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "worker",
        "publishing-worker",
    };

    private static readonly char[] GlobCharacters = ['*', '?', '[', '{'];

    /// <summary>Whether <paramref name="name"/> is an implementation specialist subject to the guard.</summary>
    public static bool IsImplementationSpecialist(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();
        return catalog.Agents.TryGetValue(name, out ArbiterSquadAgent? agent)
            && GuardedProfiles.Contains(agent.CapabilityProfile);
    }

    /// <summary>
    /// The protected directories: the folders of the plan, specification and todo index
    /// properties resolved against <paramref name="config"/>. Mirrors the
    /// <c>config.planning-dirs</c> fact so the guard and the rules cannot drift apart.
    /// Empty entries protect nothing.
    /// </summary>
    public static IReadOnlyList<string> ProtectedDirectories(KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        IReadOnlyList<ConfigRegEntry> entries = config.ConfigReg.Resolve(config.Ontology);
        string? Find(string name)
        {
            foreach (ConfigRegEntry entry in entries)
            {
                if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                {
                    return entry.Path;
                }
            }

            return null;
        }

        List<string> dirs = [];
        foreach (string? path in new[]
                 {
                     Find(ConfigRegConfig.PlanIndexProperty),
                     Find(ConfigRegConfig.SpecificationIndexProperty),
                     Find(ConfigRegConfig.TodoIndexProperty),
                 })
        {
            string directory = DirectoryOf(path);
            if (directory.Length > 0 && !dirs.Contains(directory, StringComparer.Ordinal))
            {
                dirs.Add(directory);
            }
        }

        return dirs;
    }

    /// <summary>
    /// Checks one read tool call (<c>Read</c>, <c>Grep</c>, <c>Glob</c>, <c>Bash</c>)
    /// against <paramref name="protectedDirs"/>. Path-valued inputs resolve after
    /// normalisation relative to <paramref name="repoRoot"/>; shell commands match by
    /// substring. The reason names Req 25.
    /// </summary>
    public static ReadGuardResult Check(
        string toolName,
        JsonElement toolInput,
        string repoRoot,
        IReadOnlyList<string> protectedDirs)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(repoRoot);
        ArgumentNullException.ThrowIfNull(protectedDirs);

        if (protectedDirs.Count == 0 || toolInput.ValueKind != JsonValueKind.Object)
        {
            return ReadGuardResult.Allow;
        }

        if (string.Equals(toolName, "Bash", StringComparison.Ordinal))
        {
            string? command = GetString(toolInput, "command");
            if (string.IsNullOrEmpty(command))
            {
                return ReadGuardResult.Allow;
            }

            string normalized = command.Replace('\\', '/');
            foreach (string dir in protectedDirs)
            {
                if (normalized.Contains(dir, StringComparison.Ordinal))
                {
                    return Deny(toolName, command, dir);
                }
            }

            return ReadGuardResult.Allow;
        }

        if (string.Equals(toolName, "Glob", StringComparison.Ordinal)
            && GetString(toolInput, "path") is null)
        {
            // A bare pattern with no path is checked against its literal prefix alone.
            string? pattern = GetString(toolInput, "pattern");
            if (string.IsNullOrEmpty(pattern))
            {
                return ReadGuardResult.Allow;
            }

            string literal = LiteralPrefix(pattern);
            if (literal.Length == 0)
            {
                return ReadGuardResult.Allow;
            }

            foreach (string dir in protectedDirs)
            {
                if (IsInside(literal, dir) || IsInside(dir, literal))
                {
                    return Deny(toolName, pattern, dir);
                }
            }

            return ReadGuardResult.Allow;
        }

        string? value = toolName switch
        {
            "Read" => GetString(toolInput, "file_path"),
            "Grep" => GetString(toolInput, "path"),
            "Glob" => GetString(toolInput, "path"),
            _ => null,
        };

        if (string.IsNullOrEmpty(value))
        {
            return ReadGuardResult.Allow;
        }

        string? relative = ToRepoRelative(value, repoRoot);
        if (relative is null)
        {
            return ReadGuardResult.Allow;
        }

        foreach (string dir in protectedDirs)
        {
            if (IsInside(relative, dir))
            {
                return Deny(toolName, value, dir);
            }
        }

        return ReadGuardResult.Allow;
    }

    private static ReadGuardResult Deny(string toolName, string value, string dir) =>
        new(
            false,
            $"Kyber-Weave Arbiter Read guard denied this {toolName} call: '{value}' " +
            $"resolves inside protected planning directory '{dir}' (Req 25.2, Req 25.4).");

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string LiteralPrefix(string pattern)
    {
        int glob = pattern.IndexOfAny(GlobCharacters);
        string literal = (glob < 0 ? pattern : pattern.Substring(0, glob)).TrimEnd('/');
        return NormalizeLexically(literal) ?? string.Empty;
    }

    private static string? ToRepoRelative(string value, string repoRoot)
    {
        string forward = value.Replace('\\', '/');
        if (Path.IsPathFullyQualified(forward))
        {
            string relative = Path.GetRelativePath(repoRoot, forward).Replace('\\', '/');
            if (relative.StartsWith("..", StringComparison.Ordinal))
            {
                return null;
            }

            return NormalizeLexically(relative);
        }

        return NormalizeLexically(forward);
    }

    private static string? NormalizeLexically(string path)
    {
        List<string> parts = [];
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return string.Join("/", parts);
    }

    private static bool IsInside(string path, string dir) =>
        string.Equals(path, dir, StringComparison.Ordinal)
        || path.StartsWith(dir + "/", StringComparison.Ordinal);

    private static string DirectoryOf(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string normalized = path.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized.Substring(0, slash);
    }
}

/// <summary>One Read guard verdict.</summary>
/// <param name="Allowed">True when the call names no protected path.</param>
/// <param name="Reason">The Req 25 denial reason, when denied.</param>
public sealed record ReadGuardResult(bool Allowed, string? Reason)
{
    /// <summary>The allow verdict.</summary>
    public static ReadGuardResult Allow { get; } = new(true, null);
}
