using System.Text.Json;

namespace KyberWeave.Core.Squad.Model;

/// <summary>The validated, deterministic canonical source for a Squad bundle.</summary>
public sealed record SquadSource(
    string RootPath,
    SquadManifest Manifest,
    SquadBundle Bundle,
    IReadOnlyList<SquadAgent> Agents,
    IReadOnlyList<SquadSkill> Skills,
    SquadModelProfiles ModelProfiles,
    SquadCapabilityProfiles CapabilityProfiles,
    SquadFallbackProfiles FallbackProfiles,
    SquadToolchain Toolchain,
    JsonElement McpConfiguration);

/// <summary>The root <c>squad.yml</c> manifest.</summary>
public sealed record SquadManifest(
    string Schema,
    string Name,
    string VersionSource,
    string DefaultBundle,
    IReadOnlyDictionary<string, string> Bundles,
    SquadProfilePaths Profiles,
    string ToolchainPath,
    string McpPath,
    string SourcePath);

/// <summary>Paths to the three profile documents declared by a Squad manifest.</summary>
public sealed record SquadProfilePaths(string Models, string Capabilities, string Fallbacks);

/// <summary>A named selection of canonical agents and skills.</summary>
public sealed record SquadBundle(
    string Schema,
    string Name,
    IReadOnlyList<string> AgentNames,
    IReadOnlyList<string> SkillNames,
    string SourcePath);

/// <summary>A canonical agent definition and its normalized instruction body.</summary>
public sealed record SquadAgent(
    string Schema,
    string Name,
    string Description,
    SquadInvocation Invocation,
    string ModelProfile,
    string CapabilityProfile,
    IReadOnlyList<string> DelegatesTo,
    string Fallback,
    IReadOnlyList<string> Aliases,
    string InstructionBody,
    string BodyDigest,
    string SourcePath);

/// <summary>A canonical skill definition discovered from a <c>SKILL.md</c>.</summary>
public sealed record SquadSkill(
    string Name,
    string Description,
    string InstructionBody,
    string SourcePath);

/// <summary>How an agent is invoked by a harness that can represent it natively.</summary>
public enum SquadInvocation
{
    Primary,
    Subagent
}

/// <summary>The named model profiles available to canonical agents.</summary>
public sealed record SquadModelProfiles(
    string Schema,
    IReadOnlyDictionary<string, SquadModelProfile> Profiles,
    string SourcePath);

/// <summary>A target-neutral default plus optional harness-specific model identifiers.</summary>
public sealed record SquadModelProfile(
    string Default,
    IReadOnlyDictionary<string, string> HarnessModels);

/// <summary>The semantic capability vocabulary and named permission profiles.</summary>
public sealed record SquadCapabilityProfiles(
    string Schema,
    IReadOnlyList<string> Capabilities,
    IReadOnlyDictionary<string, SquadCapabilityProfile> Profiles,
    string SourcePath);

/// <summary>Resolved permission decisions for one named capability profile.</summary>
public sealed record SquadCapabilityProfile(
    IReadOnlyDictionary<string, SquadPermissionDecision> Permissions);

/// <summary>A permission lattice ordered from most to least restrictive.</summary>
public enum SquadPermissionDecision
{
    Deny,
    Ask,
    Allow
}

/// <summary>Named lowering behavior for harnesses missing an agent primitive.</summary>
public sealed record SquadFallbackProfiles(
    string Schema,
    IReadOnlyDictionary<string, SquadFallbackProfile> Profiles,
    string SourcePath);

/// <summary>Fallback decisions and deterministic role-skill identity projection.</summary>
public sealed record SquadFallbackProfile(
    string NoPrimaryAgent,
    string NoAgentPrimitive,
    string BodySource,
    SquadFallbackOutputIdentity OutputIdentity,
    IReadOnlyList<string> SharedIdentities);

/// <summary>Names generated role skills without colliding with canonical skill identities.</summary>
public sealed record SquadFallbackOutputIdentity(
    string Unoccupied,
    string Shared,
    string Collision,
    string Prefix);

/// <summary>The upstream APM feature and validated-release gate.</summary>
public sealed record SquadToolchain(
    string Schema,
    IReadOnlyList<string> RequiredFeatures,
    JsonElement? ValidatedRelease,
    string SourcePath);
