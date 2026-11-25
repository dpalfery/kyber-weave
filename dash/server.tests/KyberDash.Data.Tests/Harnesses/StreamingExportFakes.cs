using KyberDash.Data.Collector.Sources;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>A database whose rows come from a per-query script and are yielded lazily.</summary>
internal sealed class ScriptedDatabase(
    Func<HarnessSqliteQuery, IEnumerable<IReadOnlyDictionary<string, object?>>> script) : IHarnessSqliteDatabase
{
    public IEnumerable<IReadOnlyDictionary<string, object?>> Query(HarnessSqliteQuery query) => script(query);

    public IReadOnlySet<string> GetColumns(string table) => new HashSet<string>();

    public void Dispose()
    {
    }
}

/// <summary>A buffer writer that refuses to commit more than its cap, like the reader's export buffer.</summary>
internal sealed class FailingSink(int capBytes) : System.Buffers.IBufferWriter<byte>
{
    private byte[] buffer = new byte[256];
    private int written;

    public void Advance(int count)
    {
        if (written + count > capBytes)
        {
            throw new InvalidDataException("cap");
        }

        written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return buffer.AsMemory(written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return buffer.AsSpan(written);
    }

    private void Ensure(int sizeHint)
    {
        int needed = written + Math.Max(sizeHint, 1);
        if (needed > buffer.Length)
        {
            Array.Resize(ref buffer, Math.Max(needed, buffer.Length * 2));
        }
    }
}
