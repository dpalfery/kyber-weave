using System.Text;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Shared assembly of a Markdown file with YAML frontmatter — the common document shape
/// every harness renderer emits.
/// </summary>
/// <remarks>
/// Serialize-frontmatter, fence, normalize CRLF, guarantee a trailing newline: that
/// sequence was repeated per method per renderer, and with eight more targets on the
/// doctor roster it would keep being copied. Divergence there is not hypothetical — a
/// renderer that forgets the trailing newline or emits CRLF produces byte-different
/// output for the same canonical source, which the determinism contracts exist to catch.
/// </remarks>
public static class SquadMarkdownDocument
{
    /// <summary>Composes a frontmatter + body Markdown document with normalized line endings.</summary>
    public static string Compose(
        ISerializer serializer,
        IReadOnlyDictionary<string, object?> frontmatter,
        string body)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(frontmatter);
        ArgumentNullException.ThrowIfNull(body);

        return Compose(serializer.Serialize(frontmatter), body);
    }

    /// <summary>
    /// Composes a document from frontmatter a renderer has already emitted itself, rather
    /// than from a serialized dictionary.
    /// </summary>
    /// <remarks>
    /// <see cref="ZCodeRenderer"/> is the one caller: ZCode's frontmatter reader is a
    /// hand-rolled line parser, not a YAML parser, so the emitted scalar form has to match
    /// ZCode's own writer rather than whatever a general-purpose serializer produces. The
    /// fencing, CRLF normalization, and trailing-newline guarantees are the reason this
    /// helper exists, and they are exactly what that renderer still needs to share.
    /// </remarks>
    public static string Compose(string frontmatterYaml, string body)
    {
        ArgumentNullException.ThrowIfNull(frontmatterYaml);
        ArgumentNullException.ThrowIfNull(body);

        string yaml = frontmatterYaml;
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append(yaml);
        if (!yaml.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append("---\n");

        string normalizedBody = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        builder.Append(normalizedBody);
        if (!normalizedBody.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        return builder.ToString();
    }
}
