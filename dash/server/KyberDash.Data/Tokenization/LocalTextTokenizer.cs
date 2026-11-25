using System.Collections.Concurrent;
using KyberDash.Contracts.Repositories;
using KyberDash.Domain;
using Microsoft.ML.Tokenizers;

namespace KyberDash.Data.Tokenization;

/// <summary>
/// <see cref="ITextTokenizer"/> adapter over <c>Microsoft.ML.Tokenizers</c> that counts text with the
/// model's own published tokenizer when one is known, else the labelled stand-in
/// <see cref="StandardTokenizerName"/> (decisions D17 and D19).
/// </summary>
/// <remarks>
/// <para>
/// The vocabularies come only from the <c>Microsoft.ML.Tokenizers.Data.O200kBase</c> and
/// <c>Microsoft.ML.Tokenizers.Data.Cl100kBase</c> packages this project references, which embed the
/// files in their assemblies, so no count ever reaches the network.
/// </para>
/// <para>
/// The library maps a model name to its encoding inside <c>TiktokenTokenizer.CreateForModel</c> but
/// does not expose the encoding name it chose. Each distinct model is resolved once: the tokenizer
/// <c>CreateForModel</c> builds is compared, on one probe text where the two vocabularies disagree,
/// with the two encoders this class caches, and the matching encoder's name becomes the label. That
/// keeps the library as the one authority for which model uses which encoding, rather than a
/// hand-written table here that would drift. A model the library does not recognise, or one whose
/// encoding has no shipped data package, gets the stand-in with <see cref="TokenCount.IsModelOwn"/>
/// <see langword="false"/>, labelled at the source.
/// </para>
/// </remarks>
public class LocalTextTokenizer : ITextTokenizer
{
    /// <summary>Name of the standard tokenizer that stands in for models with no published tokenizer (D19).</summary>
    public const string StandardTokenizerName = O200kBaseName;

    /// <summary>Encoding name of the current OpenAI vocabulary.</summary>
    public const string O200kBaseName = "o200k_base";

    /// <summary>Encoding name of the older OpenAI vocabulary.</summary>
    public const string Cl100kBaseName = "cl100k_base";

    private const string ProbeText = "こんにちは世界、今日はいい天気ですね。 tokenization_probe";

    private static readonly Lazy<Tokenizer> O200kBase =
        new(() => TiktokenTokenizer.CreateForEncoding(O200kBaseName), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Tokenizer> Cl100kBase =
        new(() => TiktokenTokenizer.CreateForEncoding(Cl100kBaseName), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ConcurrentDictionary<string, Lazy<Selection>> selections = new(StringComparer.Ordinal);

    /// <summary>Process-wide instance for callers that cannot take the seam through a constructor.</summary>
    public static LocalTextTokenizer Shared { get; } = new();

    /// <inheritdoc/>
    public TokenCount Count(string text, string? model)
    {
        ArgumentNullException.ThrowIfNull(text);

        Selection selection = Select(model);
        long count = text.Length == 0 ? 0 : selection.Tokenizer.CountTokens(text);
        return new TokenCount(count, selection.Name, selection.IsModelOwn);
    }

    private static Selection StandIn() => new(O200kBase.Value, StandardTokenizerName, IsModelOwn: false);

    private static Selection Resolve(string model)
    {
        Tokenizer forModel;
        try
        {
            forModel = TiktokenTokenizer.CreateForModel(model);
        }
        catch (NotSupportedException)
        {
            return StandIn();
        }
        catch (ArgumentException)
        {
            return StandIn();
        }
        catch (InvalidOperationException)
        {
            // A recognised model whose encoding has no data package referenced here.
            return StandIn();
        }

        IReadOnlyList<int> probe = forModel.EncodeToIds(ProbeText);
        if (probe.SequenceEqual(O200kBase.Value.EncodeToIds(ProbeText)))
        {
            return new Selection(O200kBase.Value, O200kBaseName, IsModelOwn: true);
        }

        if (probe.SequenceEqual(Cl100kBase.Value.EncodeToIds(ProbeText)))
        {
            return new Selection(Cl100kBase.Value, Cl100kBaseName, IsModelOwn: true);
        }

        return StandIn();
    }

    private Selection Select(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return StandIn();
        }

        string key = model.Trim();
        return selections.GetOrAdd(
            key,
            static name => new Lazy<Selection>(() => Resolve(name), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private record Selection(Tokenizer Tokenizer, string Name, bool IsModelOwn);
}
