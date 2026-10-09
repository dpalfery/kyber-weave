using System.Text;

namespace KyberWeave.Core.Arbiter.Providers;

/// <summary>
/// The pre-call state budget: how big a batched step-1 request may be for a
/// given model before the client refuses to send it.
/// </summary>
/// <remarks>
/// Tokens are estimated as ⌈UTF-8 bytes ÷ 3⌉. Over-estimating only produces
/// <c>undecidable</c>, which is the safe direction: over budget, every step-1
/// rule on the trigger answers <c>undecidable</c> and no request is sent.
/// Truncating would let the model judge evidence it never saw.
/// An unknown model gets the smallest known budget.
/// </remarks>
public static class StateBudget
{
    /// <summary>Every model: the request body is at most 64 KiB.</summary>
    public const int MaxBodyBytes = 65536;

    /// <summary>Every model: at most 64 questions per batched call.</summary>
    public const int MaxQuestions = 64;

    /// <summary><c>jev-*</c>: the shared state plus the longest question.</summary>
    public const int JevStatePlusLongestQuestionTokens = 32000;

    /// <summary><c>jev-*</c>: the whole request.</summary>
    public const int JevTotalRequestTokens = 64000;

    /// <summary><c>nimble</c>: the shared state plus each question.</summary>
    public const int NimbleStatePlusQuestionTokens = 8000;

    /// <summary><c>tev1</c>, and any unknown model: the smallest known budget.</summary>
    public const int Tev1Tokens = 2000;

    /// <summary>The smallest known budget, applied to unknown models.</summary>
    public const int SmallestKnownBudgetTokens = Tev1Tokens;

    /// <summary>Estimates tokens as ⌈UTF-8 bytes ÷ 3⌉.</summary>
    public static int EstimateTokens(int utf8Bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(utf8Bytes);
        return (utf8Bytes + 2) / 3;
    }

    /// <summary>Estimates tokens for text as ⌈UTF-8 bytes ÷ 3⌉.</summary>
    public static int EstimateTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EstimateTokens(Encoding.UTF8.GetByteCount(text));
    }

    /// <summary>Whether the batched request exceeds the model's budget.</summary>
    /// <param name="model">The answering model; unknown models get the smallest budget.</param>
    /// <param name="stateTokens">Estimated tokens of the shared state object.</param>
    /// <param name="longestQuestionTokens">Estimated tokens of the largest single question.</param>
    /// <param name="totalRequestTokens">Estimated tokens of the whole request body.</param>
    /// <param name="bodyBytes">The UTF-8 request body size in bytes.</param>
    /// <param name="questionCount">The number of batched questions.</param>
    public static bool IsOverBudget(
        string model,
        int stateTokens,
        int longestQuestionTokens,
        int totalRequestTokens,
        int bodyBytes,
        int questionCount)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (questionCount > MaxQuestions || bodyBytes > MaxBodyBytes)
        {
            return true;
        }

        if (model.StartsWith("jev-", StringComparison.OrdinalIgnoreCase))
        {
            return stateTokens + longestQuestionTokens > JevStatePlusLongestQuestionTokens
                || totalRequestTokens > JevTotalRequestTokens;
        }

        if (string.Equals(model, "nimble", StringComparison.OrdinalIgnoreCase))
        {
            return stateTokens + longestQuestionTokens > NimbleStatePlusQuestionTokens;
        }

        // tev1 and every unknown model share the smallest budget.
        return stateTokens + longestQuestionTokens > Tev1Tokens
            || totalRequestTokens > Tev1Tokens;
    }
}
