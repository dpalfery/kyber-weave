using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace KyberDash.Domain;

/// <summary>
/// Compresses and decompresses RFC 1950 zlib columns (<c>records.parts_json</c>,
/// <c>records.raw</c>, <c>session_turn.turn_json</c>), matching
/// <c>dash/src/canon/compressed-column.ts</c> and <c>compressRaw</c> in
/// <c>dash/src/canon/store-rows.ts</c>.
/// </summary>
public static class CanonCompressedColumn
{
    /// <summary>
    /// Compresses <paramref name="json"/> with zlib only when the deflated bytes are strictly
    /// shorter than the UTF-8 byte length; otherwise returns <see langword="null"/> so the
    /// caller can store plain JSON text.
    /// </summary>
    public static byte[]? CompressIfSmaller(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        byte[] utf8 = Encoding.UTF8.GetBytes(json);
        byte[] deflated = CompressBytes(utf8);
        return deflated.Length < utf8.Length ? deflated : null;
    }

    /// <summary>
    /// Always compresses <paramref name="json"/> UTF-8 bytes with zlib (matching <c>compressRaw</c>).
    /// </summary>
    public static byte[] CompressRaw(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return CompressBytes(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Maximum decompressed size accepted from a zlib column (64 MiB).</summary>
    public const int MaxDecompressedBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Decompresses RFC 1950 zlib bytes into a UTF-8 JSON string, refusing output larger than
    /// <see cref="MaxDecompressedBytes"/> so a small blob cannot expand without bound.
    /// </summary>
    /// <exception cref="InvalidDataException">The column expands past <see cref="MaxDecompressedBytes"/>.</exception>
    public static string Decompress(ReadOnlySpan<byte> deflated)
    {
        using MemoryStream input = new(deflated.ToArray());
        using ZLibStream zlib = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxDecompressedBytes)
            {
                throw new InvalidDataException(
                    string.Create(CultureInfo.InvariantCulture, $"a compressed column expands past the {MaxDecompressedBytes}-byte limit"));
            }

            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static byte[] CompressBytes(byte[] utf8)
    {
        using MemoryStream output = new();
        using (ZLibStream zlib = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(utf8, 0, utf8.Length);
        }

        return output.ToArray();
    }
}
