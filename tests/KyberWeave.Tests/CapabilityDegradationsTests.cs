using System.Collections.Generic;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Tests the shared <see cref="CapabilityDegradations"/> helper that emits a
/// <c>capability-not-isolable</c> degradation record when <c>process.execute: allow</c>
/// coexists with <c>filesystem.write: ask</c> or <c>deny</c>, naming the granted shell
/// tool(s) and withheld write tool(s) that the shell can still reach through redirection.
/// </summary>
/// <remarks>
/// The helper is called by six renderers (Claude, Pi, ZCode, Factory, OpenCode, Antigravity)
/// when their capability profiles exhibit the XOR pattern: exactly one of
/// <c>filesystem.write</c> / <c>process.execute</c> at <c>allow</c>, the other at
/// <c>ask</c> or <c>deny</c>. This test pins the helper's contract so each renderer's
/// tool-name specifics are verified in renderer-scoped contract tests (ClaudeRendererContractTests,
/// PiRendererContractTests, etc.). See the plan (2026-09-21, D10, D11) for motivation: the shell
/// grants a capability to write files via redirection, so withholding the named write tools does
/// not isolate the write capability when the shell is granted.
/// </remarks>
public sealed class CapabilityDegradationsTests
{
    /// <summary>
    /// Process.execute: allow, filesystem.write: deny returns a record naming the
    /// granted shell tool(s) and withheld write tool(s).
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecuteAllowWriteDenyTestCases))]
    public void ExecuteAllowWriteDeny_ReturnsCapabilityNotIsolableRecord(
        string targetToken,
        string canonicalIdentity,
        string outputIdentity,
        string instructionDigest,
        string[] grantedShellTools,
        string[] withheldWriteTools)
    {
        SquadDegradationRecord? result = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: targetToken,
            canonicalIdentity: canonicalIdentity,
            outputIdentity: outputIdentity,
            instructionDigest: instructionDigest,
            executeDecision: SquadPermissionDecision.Allow,
            writeDecision: SquadPermissionDecision.Deny,
            grantedShellTools: grantedShellTools,
            withheldWriteTools: withheldWriteTools);

        Assert.NotNull(result);
        Assert.Equal("capability-not-isolable", result.Code, StringComparer.Ordinal);
        Assert.Equal(targetToken, result.Target, StringComparer.Ordinal);
        Assert.Equal(canonicalIdentity, result.CanonicalIdentity, StringComparer.Ordinal);
        Assert.Equal(outputIdentity, result.OutputIdentity, StringComparer.Ordinal);
        Assert.Equal(instructionDigest, result.InstructionDigest, StringComparer.Ordinal);

        // The record names all granted shell tools.
        foreach (string shellTool in grantedShellTools)
        {
            Assert.Contains(
                shellTool,
                result.Details,
                StringComparison.Ordinal);
        }

        // The record names all withheld write tools.
        foreach (string writeTool in withheldWriteTools)
        {
            Assert.Contains(
                writeTool,
                result.Details,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Process.execute: allow, filesystem.write: ask returns a record naming the
    /// granted shell tool(s) and withheld write tool(s).
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecuteAllowWriteAskTestCases))]
    public void ExecuteAllowWriteAsk_ReturnsCapabilityNotIsolableRecord(
        string targetToken,
        string canonicalIdentity,
        string outputIdentity,
        string instructionDigest,
        string[] grantedShellTools,
        string[] withheldWriteTools)
    {
        SquadDegradationRecord? result = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: targetToken,
            canonicalIdentity: canonicalIdentity,
            outputIdentity: outputIdentity,
            instructionDigest: instructionDigest,
            executeDecision: SquadPermissionDecision.Allow,
            writeDecision: SquadPermissionDecision.Ask,
            grantedShellTools: grantedShellTools,
            withheldWriteTools: withheldWriteTools);

        Assert.NotNull(result);
        Assert.Equal("capability-not-isolable", result.Code, StringComparer.Ordinal);
        Assert.Equal(targetToken, result.Target, StringComparer.Ordinal);
        Assert.Equal(canonicalIdentity, result.CanonicalIdentity, StringComparer.Ordinal);
        Assert.Equal(outputIdentity, result.OutputIdentity, StringComparer.Ordinal);
        Assert.Equal(instructionDigest, result.InstructionDigest, StringComparer.Ordinal);

        // The record names all granted shell tools.
        foreach (string shellTool in grantedShellTools)
        {
            Assert.Contains(
                shellTool,
                result.Details,
                StringComparison.Ordinal);
        }

        // The record names all withheld write tools.
        foreach (string writeTool in withheldWriteTools)
        {
            Assert.Contains(
                writeTool,
                result.Details,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Process.execute: allow, filesystem.write: allow returns null.
    /// The XOR pattern does not hold when both are allowed.
    /// </summary>
    [Theory]
    [InlineData("claude", "test-agent", "test-agent", "digest123")]
    [InlineData("pi", "test-agent", "test-agent", "digest123")]
    [InlineData("zcode", "test-agent", "test-agent", "digest123")]
    public void ExecuteAllowWriteAllow_ReturnsNull(
        string targetToken,
        string canonicalIdentity,
        string outputIdentity,
        string instructionDigest)
    {
        SquadDegradationRecord? result = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: targetToken,
            canonicalIdentity: canonicalIdentity,
            outputIdentity: outputIdentity,
            instructionDigest: instructionDigest,
            executeDecision: SquadPermissionDecision.Allow,
            writeDecision: SquadPermissionDecision.Allow,
            grantedShellTools: ["bash"],
            withheldWriteTools: ["edit"]);

        Assert.Null(result);
    }

    /// <summary>
    /// Process.execute: deny, filesystem.write: deny returns null.
    /// The helper only fires when execute is allow.
    /// </summary>
    [Theory]
    [InlineData("claude", "test-agent", "test-agent", "digest123")]
    [InlineData("pi", "test-agent", "test-agent", "digest123")]
    [InlineData("antigravity", "test-agent", "test-agent", "digest123")]
    public void ExecuteDenyWriteDeny_ReturnsNull(
        string targetToken,
        string canonicalIdentity,
        string outputIdentity,
        string instructionDigest)
    {
        SquadDegradationRecord? result = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: targetToken,
            canonicalIdentity: canonicalIdentity,
            outputIdentity: outputIdentity,
            instructionDigest: instructionDigest,
            executeDecision: SquadPermissionDecision.Deny,
            writeDecision: SquadPermissionDecision.Deny,
            grantedShellTools: ["bash"],
            withheldWriteTools: ["edit"]);

        Assert.Null(result);
    }

    /// <summary>
    /// Process.execute: ask, filesystem.write: deny returns null.
    /// The helper only fires when execute is allow.
    /// </summary>
    [Theory]
    [InlineData("claude", "test-agent", "test-agent", "digest123")]
    [InlineData("factory", "test-agent", "test-agent", "digest123")]
    [InlineData("opencode", "test-agent", "test-agent", "digest123")]
    public void ExecuteAskWriteDeny_ReturnsNull(
        string targetToken,
        string canonicalIdentity,
        string outputIdentity,
        string instructionDigest)
    {
        SquadDegradationRecord? result = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: targetToken,
            canonicalIdentity: canonicalIdentity,
            outputIdentity: outputIdentity,
            instructionDigest: instructionDigest,
            executeDecision: SquadPermissionDecision.Ask,
            writeDecision: SquadPermissionDecision.Deny,
            grantedShellTools: ["bash"],
            withheldWriteTools: ["edit"]);

        Assert.Null(result);
    }

    /// <summary>
    /// When execute is allow and write is deny but no write tools are withheld,
    /// the record is still emitted (though the tool list is empty). This covers
    /// the edge case where a capability is withheld but no specific named tools
    /// correspond to it (unlikely in practice, but part of the contract).
    /// </summary>
    [Fact]
    public void ExecuteAllowWriteDenyNoWriteTools_ReturnsRecord()
    {
        SquadDegradationRecord? result = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: "claude",
            canonicalIdentity: "test-agent",
            outputIdentity: "test-agent",
            instructionDigest: "digest123",
            executeDecision: SquadPermissionDecision.Allow,
            writeDecision: SquadPermissionDecision.Deny,
            grantedShellTools: ["Bash"],
            withheldWriteTools: []);

        Assert.NotNull(result);
        Assert.Equal("capability-not-isolable", result.Code, StringComparer.Ordinal);
        Assert.Equal("claude", result.Target, StringComparer.Ordinal);
        Assert.Equal("test-agent", result.CanonicalIdentity, StringComparer.Ordinal);
        Assert.Equal("test-agent", result.OutputIdentity, StringComparer.Ordinal);
        Assert.Equal("digest123", result.InstructionDigest, StringComparer.Ordinal);
        Assert.Contains("Bash", result.Details, StringComparison.Ordinal);
    }

    /// <summary>
    /// When execute is allow and write is deny but no shell tools are granted,
    /// returns null. Per D10 (plan 2026-09-21, 3), the record names "the granted
    /// shell tool and the withheld write tools it can still reach through redirection".
    /// With no granted shell, there is nothing to reach through, so no record.
    /// </summary>
    [Fact]
    public void ExecuteAllowWriteDenyNoShellTools_ReturnsNull()
    {
        SquadDegradationRecord? result = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: "pi",
            canonicalIdentity: "test-agent",
            outputIdentity: "test-agent",
            instructionDigest: "digest123",
            executeDecision: SquadPermissionDecision.Allow,
            writeDecision: SquadPermissionDecision.Deny,
            grantedShellTools: [],
            withheldWriteTools: ["edit", "write"]);

        Assert.Null(result);
    }

    /// <summary>
    /// Test case data: execute allow, write deny, various tool combinations for Claude.
    /// </summary>
    public static IEnumerable<object[]> ExecuteAllowWriteDenyTestCases
    {
        get
        {
            yield return new object[]
            {
                "claude",
                "investigator",
                "investigator",
                "investigator_digest",
                new[] { "Bash", "PowerShell" },
                new[] { "Edit", "Write", "NotebookEdit" }
            };
            yield return new object[]
            {
                "pi",
                "investigator",
                "investigator",
                "investigator_digest",
                new[] { "bash" },
                new[] { "edit", "write" }
            };
            yield return new object[]
            {
                "zcode",
                "investigator",
                "investigator",
                "investigator_digest",
                new[] { "Bash" },
                new[] { "Edit", "Write" }
            };
            yield return new object[]
            {
                "factory",
                "investigator",
                "investigator",
                "investigator_digest",
                new[] { "Execute" },
                new[] { "Create", "Edit", "ApplyPatch" }
            };
            yield return new object[]
            {
                "opencode",
                "investigator",
                "investigator",
                "investigator_digest",
                new[] { "bash" },
                new[] { "edit" }
            };
            yield return new object[]
            {
                "antigravity",
                "investigator",
                "investigator",
                "investigator_digest",
                new[] { "run_command" },
                new[] { "write_to_file", "replace_file_content", "multi_replace_file_content" }
            };
        }
    }

    /// <summary>
    /// Test case data: execute allow, write ask, various tool combinations.
    /// </summary>
    public static IEnumerable<object[]> ExecuteAllowWriteAskTestCases
    {
        get
        {
            yield return new object[]
            {
                "claude",
                "reviewer",
                "reviewer",
                "reviewer_digest",
                new[] { "Bash" },
                new[] { "Edit", "Write" }
            };
            yield return new object[]
            {
                "pi",
                "reviewer",
                "reviewer",
                "reviewer_digest",
                new[] { "bash" },
                new[] { "edit", "write" }
            };
            yield return new object[]
            {
                "antigravity",
                "reviewer",
                "reviewer",
                "reviewer_digest",
                new[] { "run_command" },
                new[] { "write_to_file", "replace_file_content" }
            };
        }
    }
}
