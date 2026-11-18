using System.Globalization;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>An identifier generator a test can predict: the nth call returns the nth value.</summary>
public sealed class FakeIdGenerator : IIdGenerator
{
    private int _next;

    /// <summary>Creates a generator whose first value is one.</summary>
    public FakeIdGenerator()
    {
    }

    /// <summary>Creates a generator that starts counting at a chosen value.</summary>
    /// <param name="firstValue">The number the first value carries.</param>
    public FakeIdGenerator(int firstValue) => _next = firstValue - 1;

    /// <inheritdoc />
    public Guid NewGuid()
    {
        int value = Next();
        return new Guid(value, 0, 0, new byte[8]);
    }

    /// <inheritdoc />
    public string NewRandomToken() => "token-" + Next().ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string NewRandomFileName() => "file-" + Next().ToString(CultureInfo.InvariantCulture) + ".tmp";

    private int Next() => Interlocked.Increment(ref _next);
}
