using System.Text.Json;
using System.Text.Json.Nodes;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>How a harness stores the MCP servers it launches.</summary>
public enum McpConfigurationShape
{
    /// <summary>A JSON document whose server entries live under a key path.</summary>
    Json,

    /// <summary>
    /// A TOML document declaring one <c>[mcp_servers.&lt;name&gt;]</c> table per server.
    /// </summary>
    Toml,

    /// <summary>
    /// Repository settings on the hosting service. There is no file in the tree to read or
    /// write, which is why this row can never be wired by a repair.
    /// </summary>
    RepositorySettings
}

/// <summary>How a harness's JSON server entry holds the launch command.</summary>
/// <remarks>
/// This describes the <em>JSON</em> entry body. A TOML row carries it because it has no JSON body to
/// describe, and its reader reads the table's <c>command</c> regardless; which is why the check
/// decides what to compare from the read it made and not from this value. Two rows share
/// <see cref="McpEntryCommandShape.Undetermined"/> and only one of them has a body nothing reads.
/// </remarks>
public enum McpEntryCommandShape
{
    /// <summary>
    /// This row's JSON entry body is either absent (repository settings) or unpinned in this
    /// repository (ZCode), so a wired entry is compared by name only.
    /// </summary>
    Undetermined,

    /// <summary>The entry splits the executable from its arguments: <c>command</c>, <c>args</c>.</summary>
    CommandWithArgs,

    /// <summary>The entry holds the executable and its arguments in one <c>command</c> array.</summary>
    SingleArray
}

/// <summary>
/// One harness's repository-scope MCP configuration: where it lives, what shape it takes, what
/// puts the harness in play, and whether the repair may write it.
/// </summary>
/// <remarks>
/// Every row below is a claim about another product's file layout, so each one says where it
/// came from when that matters. The ZCode row is cited from
/// <c>KyberWeave.Core.Squad.Deployment.ZCodeMcpConfiguration</c>, which was verified against
/// zai-org/ZCode 3.14.0 on 2026-09-21; the JSON rows are the shapes recorded in the DocGraph MCP
/// runbook. A row that has not been verified is marked snippet-only rather than guessed at:
/// inventing an entry body for a harness produces a configuration that harness cannot load,
/// which is a worse outcome than telling the operator to paste it.
/// </remarks>
public sealed record HarnessMcpEntry
{
    /// <summary>The harness slug: the name findings carry and <c>--target</c> accepts.</summary>
    public required string Harness { get; init; }

    /// <summary>How the harness stores its servers.</summary>
    public required McpConfigurationShape Shape { get; init; }

    /// <summary>
    /// The repository-relative configuration files, most canonical first. Order matters: a
    /// finding names the first one that exists, and the canonical one when none does.
    /// </summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>
    /// The key path from the document root to the map of servers, for JSON rows, or the TOML
    /// table prefix for a TOML row.
    /// </summary>
    public required IReadOnlyList<string> ServerKeyPath { get; init; }

    /// <summary>How an entry exposes the launch command.</summary>
    public required McpEntryCommandShape CommandShape { get; init; }

    /// <summary>
    /// Directories whose presence puts the harness in play even with no configuration file.
    /// Empty where no marker exists beyond the file itself — Claude Code and opencode.
    /// </summary>
    public required IReadOnlyList<string> MarkerDirectories { get; init; }

    /// <summary>
    /// Whether this harness resolves its files by walking from the working directory up to the
    /// git worktree root rather than by reading each <see cref="Paths"/> entry under the repository
    /// root. ZCode is the only harness that does, and its walk is the verified one.
    /// </summary>
    public bool WalksToWorktreeRoot { get; init; }

    /// <summary>
    /// What the not-detectable line names as the thing that was looked for: the marker directory
    /// where one exists, the configuration file where it does not.
    /// </summary>
    public required string InPlaySignal { get; init; }

    /// <summary>Whether the repair may write this harness's repository-scope configuration.</summary>
    public required McpWiringFixability Fixability { get; init; }

    /// <summary>
    /// The document the repair writes when the file is absent, for a writable harness — the
    /// runbook's documented example, exactly.
    /// </summary>
    public string? CanonicalDocument { get; init; }

    /// <summary>
    /// One server entry in this harness's shape, for a writable harness. The repair builds a new
    /// entry from this rather than by re-serializing the file it is inserting into.
    /// </summary>
    public string? CanonicalEntry { get; init; }

    /// <summary>
    /// This harness's own documented spelling of "this repository": <c>${workspaceFolder}</c> for
    /// VS Code, <see cref="JsonServerSplice.RepositoryRootToken"/> for a harness that substitutes
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both sides of the round trip read it from here, which is the point. The repair substitutes
    /// this token when it composes an entry (5.11), and the check has to accept it as the
    /// declaration's <c>.</c> — otherwise the check rejects the file the repair just wrote, turning
    /// one <c>ok</c> line into a warning (3.5) and a second run of <c>mcp-wire</c> into
    /// <c>KW-MCP-WIRE-005</c> against its own success. A tolerance written twice would drift the
    /// moment a harness is added; one spelling per harness, decided in one place, cannot.
    /// </para>
    /// <para>
    /// It is read from the canonical entry's environment block rather than declared beside it
    /// because that block <em>is</em> where the runbook's example spells its root; a second literal
    /// would be a second thing to keep true. The value is the one held by the entry the registry
    /// names for the repository root — not whichever value comes first: key order is the template
    /// author's choice, and a block that lists another variable first would have made that
    /// variable's value the spelling the writer substitutes and the check accepts. A harness whose
    /// template carries no such entry substitutes nothing, so the declaration's own token is what it
    /// keeps.
    /// </para>
    /// </remarks>
    public string RepositoryRootToken
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CanonicalEntry))
                return JsonServerSplice.RepositoryRootToken;

            JsonObject? template;
            try
            {
                template = JsonNode.Parse(CanonicalEntry) as JsonObject;
            }
            catch (JsonException)
            {
                return JsonServerSplice.RepositoryRootToken;
            }

            if (template is null)
                return JsonServerSplice.RepositoryRootToken;

            foreach (KeyValuePair<string, JsonNode?> property in template)
            {
                if (!IsEnvironmentKey(property.Key) || property.Value is not JsonObject environment)
                    continue;

                if (environment[RepositoryRootEnvironmentKey] is JsonValue value
                    && value.TryGetValue(out string? text)
                    && !string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }

            return JsonServerSplice.RepositoryRootToken;
        }
    }

    /// <summary>
    /// The environment key the repository-root spelling travels in: the registry's own name for the
    /// repository root, and the key every catalog template holds its root under.
    /// </summary>
    /// <remarks>
    /// Spelled here rather than referenced from <c>KyberWeave.Mcp.RepositoryRootResolver</c> because
    /// Core does not reference the host that resolves a root from it; the variable's name is the
    /// shared contract, and both sides spell it.
    /// </remarks>
    private const string RepositoryRootEnvironmentKey = "KYBER_WEAVE_REPO_ROOT";

    /// <summary>
    /// Whether a key of an entry body is the block a harness spells its repository root in:
    /// <c>env</c> for most, <c>environment</c> for opencode.
    /// </summary>
    internal static bool IsEnvironmentKey(string key) => key is "env" or "environment";
}

/// <summary>
/// The harnesses this repository knows how to check and, for five of them, how to wire.
/// </summary>
/// <remarks>
/// <para>
/// Detection is presence-based and honest about being a heuristic: a marker says "worth
/// reporting on", not "this person runs agents in this harness". Every VS Code user has a
/// <c>.vscode/</c> directory regardless of MCP, so the report never claims certainty it does not
/// have — an undetectable harness is an <c>info</c> line, never a warning.
/// </para>
/// <para>
/// The writable set is deliberately narrower than the detectable set. Codex's repository-scope
/// relative-path semantics are unverified, ZCode's entry body inside <c>mcp.servers</c> is
/// pinned nowhere in this repository, and the Copilot coding agent has no file to write at all,
/// so those three are checked and reported but never written. Widening one is a catalog edit
/// plus its verified fact, not a structural change.
/// </para>
/// </remarks>
public static class HarnessMcpCatalog
{
    /// <summary>The harnesses in report order. Findings are emitted in this order.</summary>
    public static IReadOnlyList<HarnessMcpEntry> Rows { get; } =
    [
        new HarnessMcpEntry
        {
            Harness = "factory",
            Shape = McpConfigurationShape.Json,
            Paths = [".factory/mcp.json"],
            ServerKeyPath = ["mcpServers"],
            CommandShape = McpEntryCommandShape.CommandWithArgs,
            MarkerDirectories = [".factory"],
            InPlaySignal = ".factory",
            Fixability = McpWiringFixability.Fixable,
            CanonicalDocument =
                """
                {
                  "mcpServers": {
                    "kyber-weave": {
                      "command": "kyber-weave-mcp",
                      "args": ["--repo-root", "."]
                    }
                  }
                }
                """,
            CanonicalEntry =
                """
                {
                  "command": "kyber-weave-mcp",
                  "args": ["--repo-root", "."]
                }
                """
        },
        new HarnessMcpEntry
        {
            Harness = "claude-code",
            Shape = McpConfigurationShape.Json,
            Paths = [".mcp.json"],
            ServerKeyPath = ["mcpServers"],
            CommandShape = McpEntryCommandShape.CommandWithArgs,
            // No marker beyond the file: "absent" means unknown here, not "not in play".
            MarkerDirectories = [],
            InPlaySignal = ".mcp.json",
            Fixability = McpWiringFixability.Fixable,
            CanonicalDocument =
                """
                {
                  "mcpServers": {
                    "kyber-weave": {
                      "type": "stdio",
                      "command": "kyber-weave-mcp",
                      "args": ["--repo-root", "."]
                    }
                  }
                }
                """,
            CanonicalEntry =
                """
                {
                  "type": "stdio",
                  "command": "kyber-weave-mcp",
                  "args": ["--repo-root", "."]
                }
                """
        },
        new HarnessMcpEntry
        {
            Harness = "cursor",
            Shape = McpConfigurationShape.Json,
            Paths = [".cursor/mcp.json"],
            ServerKeyPath = ["mcpServers"],
            CommandShape = McpEntryCommandShape.CommandWithArgs,
            MarkerDirectories = [".cursor"],
            InPlaySignal = ".cursor",
            Fixability = McpWiringFixability.Fixable,
            CanonicalDocument =
                """
                {
                  "mcpServers": {
                    "kyber-weave": {
                      "command": "kyber-weave-mcp",
                      "args": ["--repo-root", "."]
                    }
                  }
                }
                """,
            CanonicalEntry =
                """
                {
                  "command": "kyber-weave-mcp",
                  "args": ["--repo-root", "."]
                }
                """
        },
        new HarnessMcpEntry
        {
            Harness = "vscode",
            Shape = McpConfigurationShape.Json,
            Paths = [".vscode/mcp.json"],
            // Not `mcpServers`: VS Code keys the map `servers`, and the wrong key fails silently.
            ServerKeyPath = ["servers"],
            CommandShape = McpEntryCommandShape.CommandWithArgs,
            MarkerDirectories = [".vscode"],
            InPlaySignal = ".vscode",
            Fixability = McpWiringFixability.Fixable,
            CanonicalDocument =
                """
                {
                  "servers": {
                    "kyber-weave": {
                      "type": "stdio",
                      "command": "kyber-weave-mcp",
                      "args": ["--repo-root", "${workspaceFolder}"],
                      "env": {
                        "KYBER_WEAVE_REPO_ROOT": "${workspaceFolder}"
                      }
                    }
                  }
                }
                """,
            CanonicalEntry =
                """
                {
                  "type": "stdio",
                  "command": "kyber-weave-mcp",
                  "args": ["--repo-root", "${workspaceFolder}"],
                  "env": {
                    "KYBER_WEAVE_REPO_ROOT": "${workspaceFolder}"
                  }
                }
                """
        },
        new HarnessMcpEntry
        {
            Harness = "copilot-coding-agent",
            Shape = McpConfigurationShape.RepositorySettings,
            Paths = [],
            ServerKeyPath = ["mcpServers"],
            CommandShape = McpEntryCommandShape.Undetermined,
            MarkerDirectories = [],
            InPlaySignal = "repository settings",
            Fixability = McpWiringFixability.NotFixable
        },
        new HarnessMcpEntry
        {
            Harness = "codex",
            Shape = McpConfigurationShape.Toml,
            Paths = [".codex/config.toml"],
            ServerKeyPath = ["mcp_servers"],
            CommandShape = McpEntryCommandShape.Undetermined,
            MarkerDirectories = [".codex"],
            InPlaySignal = ".codex",
            // Snippet-only until Codex's repository-scope relative-path semantics are verified:
            // the runbook's verified form is an absolute path, which cannot go into a committed
            // file without breaking every other teammate.
            Fixability = McpWiringFixability.NotFixable
        },
        new HarnessMcpEntry
        {
            Harness = "opencode",
            Shape = McpConfigurationShape.Json,
            Paths = ["opencode.json"],
            ServerKeyPath = ["mcp"],
            CommandShape = McpEntryCommandShape.SingleArray,
            MarkerDirectories = [],
            InPlaySignal = "opencode.json",
            Fixability = McpWiringFixability.Fixable,
            CanonicalDocument =
                """
                {
                  "$schema": "https://opencode.ai/config.json",
                  "mcp": {
                    "kyber-weave": {
                      "type": "local",
                      "command": ["kyber-weave-mcp", "--repo-root", "."],
                      "enabled": true,
                      "environment": {
                        "KYBER_WEAVE_REPO_ROOT": "."
                      }
                    }
                  }
                }
                """,
            CanonicalEntry =
                """
                {
                  "type": "local",
                  "command": ["kyber-weave-mcp", "--repo-root", "."],
                  "enabled": true,
                  "environment": {
                    "KYBER_WEAVE_REPO_ROOT": "."
                  }
                }
                """
        },
        new HarnessMcpEntry
        {
            Harness = "zcode",
            Shape = McpConfigurationShape.Json,
            // The project tiers, working directory to worktree root. The user tier under the
            // ZCode storage root is never read (R2.6): a server wired only there is not wired for
            // the repository, which is the right report for a committed configuration.
            Paths = ["zcode.json", ".zcode/config.json"],
            ServerKeyPath = ["mcp", "servers"],
            CommandShape = McpEntryCommandShape.Undetermined,
            MarkerDirectories = [".zcode"],
            InPlaySignal = ".zcode",
            WalksToWorktreeRoot = true,
            // Snippet-only until the entry body inside `mcp.servers` is pinned against ZCode's
            // own `mcpSchema`; the tier paths and key path are verified, the body is not.
            Fixability = McpWiringFixability.NotFixable
        }
    ];

    /// <summary>Finds a row by its harness slug, or null when the slug is not in the catalog.</summary>
    public static HarnessMcpEntry? Find(string harness)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);

        foreach (HarnessMcpEntry row in Rows)
        {
            if (string.Equals(row.Harness, harness, StringComparison.Ordinal))
                return row;
        }

        return null;
    }
}
