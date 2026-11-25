using KyberDash.Contracts.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application.Tests.Fakes;

/// <summary>
/// In-memory <see cref="ITextTokenizer"/> for unit tests: one token per whitespace-separated word,
/// labelled with a fixed tokenizer name, and a record of every model it was asked to count for.
/// </summary>
/// <remarks>
/// Counting words rather than characters keeps every figure a test pins visibly different from
/// characters / 4, so a test cannot pass on the retired approximation by accident.
/// </remarks>
/// <param name="tokenizer">Tokenizer name every count carries.</param>
/// <param name="isModelOwn">Whether every count claims to be the model's own tokenizer.</param>
public class FakeTextTokenizer(string tokenizer = "fake_tokenizer", bool isModelOwn = false) : ITextTokenizer
{
    private readonly List<string?> models = [];
    private readonly List<(string Text, string? Model)> calls = [];
    private readonly Lock gate = new();

    /// <summary>Models passed to <see cref="Count"/>, in call order.</summary>
    public IReadOnlyList<string?> Models
    {
        get
        {
            lock (gate)
            {
                return [.. models];
            }
        }
    }

    /// <summary>Every text passed to <see cref="Count"/> with the model it was counted for, in call order.</summary>
    public IReadOnlyList<(string Text, string? Model)> Calls
    {
        get
        {
            lock (gate)
            {
                return [.. calls];
            }
        }
    }

    /// <inheritdoc/>
    public TokenCount Count(string text, string? model)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (gate)
        {
            models.Add(model);
            calls.Add((text, model));
        }

        long count = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LongLength;
        return new TokenCount(count, tokenizer, isModelOwn);
    }
}
