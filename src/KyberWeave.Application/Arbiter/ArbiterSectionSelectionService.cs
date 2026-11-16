using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Sections;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Application.Arbiter;

/// <summary>
/// Reads a document under the section selector's size cap and hands its text to the pure
/// <see cref="SectionSelector"/>.
/// </summary>
/// <remarks>
/// The selector itself does no I/O: the read and the byte cap live here, and a document that cannot be read or is over
/// the cap selects nothing and fails safe, which is what the caller falls back from.
/// </remarks>
/// <param name="files">Reads the document under a byte bound.</param>
public sealed class ArbiterSectionSelectionService(IBoundedFileReader files) : IArbiterSectionSelectionService
{
    /// <inheritdoc />
    public async Task<SectionSelection> SelectAsync(
        string documentPath,
        string task,
        IArbiterProvider provider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        string? text;
        try
        {
            ct.ThrowIfCancellationRequested();
            text = files.ReadBytes(documentPath, (int)SectionSelector.MaxDocumentBytes) is { } bytes
                ? Decode(bytes)
                : null;
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidDataException)
        {
            return new SectionSelection([], FailedSafe: true);
        }

        if (text is null)
        {
            return new SectionSelection([], FailedSafe: true);
        }

        return await new SectionSelector(provider).SelectAsync(text, task, ct).ConfigureAwait(false);
    }

    /// <summary>The document text, with a byte-order mark detected as <see cref="File.ReadAllText(string)"/> detects it.</summary>
    private static string Decode(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
