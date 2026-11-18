namespace KyberWeave.Core.Review;

/// <summary>One decision-model answer as the cycle's answer file keeps it.</summary>
/// <remarks>The property names are the file's format: <c>Answer</c>, <c>Score</c>, <c>Threshold</c>.</remarks>
public sealed record ReviewCachedAnswer(string Answer, double? Score, double? Threshold);
