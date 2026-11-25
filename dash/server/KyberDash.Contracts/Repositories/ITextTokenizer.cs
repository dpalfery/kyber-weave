using KyberDash.Domain;

namespace KyberDash.Contracts.Repositories;

/// <summary>
/// Counts the tokens in a piece of text with a local tokenizer chosen by model (decision D17).
/// </summary>
/// <remarks>
/// The tokenizer is an outside resource (a shipped vocabulary), so it sits behind this seam: the
/// adapter lives in <c>KyberDash.Data</c> and reads only the vocabularies its NuGet data packages
/// ship, never the network. Every count it returns names the tokenizer that produced it and whether
/// that tokenizer is the model's own or the labelled stand-in (D19), so a caller never has to infer
/// the label later.
/// </remarks>
public interface ITextTokenizer
{
    /// <summary>Counts the tokens in <paramref name="text"/> for <paramref name="model"/>.</summary>
    /// <param name="text">Text to count; an empty string counts zero.</param>
    /// <param name="model">Model the text was sent to, or <see langword="null"/> or blank when unknown.</param>
    /// <returns>The count, labelled with the tokenizer name and whether it is the model's own.</returns>
    TokenCount Count(string text, string? model);
}
