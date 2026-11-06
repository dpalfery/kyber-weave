namespace KyberWeave.Core.Configuration;

/// <summary>
/// The configuration keys under which Kyber-Weave's MCP servers are declared. A key names a
/// server in a harness's MCP configuration, in a roster, or in a grant; it is not the name of the
/// executable that serves it, and the executables keep their own names wherever they are launched.
/// </summary>
/// <remarks>
/// Only keys belong here. A binary name or a tool name beside a key would let one rename read as
/// the other, which is the confusion this type exists to remove.
/// </remarks>
public static class McpServerNames
{
    /// <summary>
    /// The documentation and governance server: the key a roster, a grant, and a harness's
    /// configuration all name it under.
    /// </summary>
    public const string Docs = "kw-docs";

    /// <summary>
    /// The KyberDash analysis server. It joins no roster, configuration, or grant; the key is
    /// declared so no other surface has to spell it.
    /// </summary>
    public const string Dash = "kw-dash";

    /// <summary>The Arbiter decision server, granted on its own entitlement and never as a default.</summary>
    public const string Arbiter = "kw-arbiter";
}
