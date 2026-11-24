using System.Diagnostics.CodeAnalysis;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>The Kyber standards templates this build ships, read through the embedded-resource seam.</summary>
/// <remarks>
/// The read is a seam so a unit test supplies the template text and asserts what the caller does with it. The folders
/// and file names themselves are fixed facts: they stay on <see cref="KyberStandardsTemplates"/>.
/// </remarks>
public interface IKyberStandardsTemplates
{
    /// <summary>Attempts to retrieve the raw embedded standard for <paramref name="technology"/>.</summary>
    /// <param name="technology">The technology slug, e.g. <c>csharp</c>.</param>
    /// <param name="content">The raw template if the build ships one; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a template exists for the technology; otherwise, <see langword="false"/>.</returns>
    bool TryGet(string? technology, [NotNullWhen(true)] out string? content);

    /// <summary>Attempts to retrieve one file of a folder's embedded template.</summary>
    /// <param name="technology">The technology, role or platform slug.</param>
    /// <param name="part">Which file of the folder to read.</param>
    /// <param name="content">The raw file if the build ships one; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the file exists; otherwise, <see langword="false"/>.</returns>
    bool TryGetPart(
        string? technology,
        KyberStandardsTemplates.Part part,
        [NotNullWhen(true)] out string? content);

    /// <summary>Attempts to render the standard for <paramref name="technology"/> with the host's owner and date.</summary>
    /// <param name="technology">The technology slug.</param>
    /// <param name="owner">The owner value to inject into frontmatter.</param>
    /// <param name="lastReviewed">The last-reviewed ISO date (<c>yyyy-MM-dd</c>) to inject.</param>
    /// <param name="rendered">The rendered template if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a template exists and was rendered; otherwise, <see langword="false"/>.</returns>
    bool TryRender(
        string? technology,
        string owner,
        string lastReviewed,
        [NotNullWhen(true)] out string? rendered);

    /// <summary>Renders the standard for <paramref name="technology"/>, or throws naming the nearest slug.</summary>
    /// <param name="technology">The technology slug.</param>
    /// <param name="owner">The owner value to inject into frontmatter.</param>
    /// <param name="lastReviewed">The last-reviewed ISO date (<c>yyyy-MM-dd</c>) to inject.</param>
    /// <returns>The rendered template.</returns>
    /// <exception cref="ArgumentException"><paramref name="technology"/> names no shipped template.</exception>
    string Render(string technology, string owner, string lastReviewed);

    /// <summary>Attempts to render one file of a folder's template.</summary>
    /// <param name="technology">The technology, role or platform slug.</param>
    /// <param name="part">Which file of the folder to render.</param>
    /// <param name="owner">The owner value to inject into frontmatter.</param>
    /// <param name="lastReviewed">The last-reviewed ISO date (<c>yyyy-MM-dd</c>) to inject.</param>
    /// <param name="rendered">The rendered file if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the file exists and was rendered; otherwise, <see langword="false"/>.</returns>
    bool TryRenderPart(
        string? technology,
        KyberStandardsTemplates.Part part,
        string owner,
        string lastReviewed,
        [NotNullWhen(true)] out string? rendered);

    /// <summary>Renders one file of a folder's template, or throws naming the nearest slug.</summary>
    /// <param name="technology">The technology, role or platform slug.</param>
    /// <param name="part">Which file of the folder to render.</param>
    /// <param name="owner">The owner value to inject into frontmatter.</param>
    /// <param name="lastReviewed">The last-reviewed ISO date (<c>yyyy-MM-dd</c>) to inject.</param>
    /// <returns>The rendered file.</returns>
    /// <exception cref="ArgumentException"><paramref name="technology"/> names no shipped folder.</exception>
    string RenderPart(
        string technology,
        KyberStandardsTemplates.Part part,
        string owner,
        string lastReviewed);

    /// <summary>Attempts to render the shared rules file that sits beside a host's standards folders.</summary>
    /// <param name="owner">The owner value to inject into frontmatter.</param>
    /// <param name="lastReviewed">The last-reviewed ISO date (<c>yyyy-MM-dd</c>) to inject.</param>
    /// <param name="rendered">The rendered file if the build ships one; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the file exists and was rendered; otherwise, <see langword="false"/>.</returns>
    bool TryRenderSharedRules(string owner, string lastReviewed, [NotNullWhen(true)] out string? rendered);

    /// <summary>Attempts to read every file of one shipped KW skill, so <c>docs init</c> can copy it into a repository.</summary>
    /// <param name="name">The skill's name, which is its folder name.</param>
    /// <param name="files">The skill's files, <c>SKILL.md</c> among them; empty when the build ships no such skill.</param>
    /// <returns><see langword="true"/> if the build ships a skill of that name; otherwise, <see langword="false"/>.</returns>
    bool TryReadShippedSkill(string name, out IReadOnlyList<ShippedSkillFile> files);
}

/// <summary>One file of a shipped KW skill.</summary>
/// <param name="RelativePath">The path inside the skill's folder, slash-separated.</param>
/// <param name="Bytes">The file's content, exactly as shipped.</param>
public sealed record ShippedSkillFile(string RelativePath, ReadOnlyMemory<byte> Bytes);
