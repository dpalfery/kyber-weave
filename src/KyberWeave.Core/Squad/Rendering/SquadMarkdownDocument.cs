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

        string yaml = serializer.Serialize(frontmatter);
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append(yaml);
        if (!yaml.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append("---\n");

        string normalizedBody = body.Replace("\r\n", "\n");
        builder.Append(normalizedBody);
        if (!normalizedBody.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        return builder.ToString();
    }
}
