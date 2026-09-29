using KyberWeave.Core.Utilities.StatusLine;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>Maps the CLI's harness tokens to the three status-line targets this slice keeps.</summary>
/// <remarks>
///     A closed vocabulary mirroring <see cref="StatusLineTarget" />, kept in the CLI rather than in Core
///     because it is presentation: the token text an operator types and reads, not a domain value. D9
///     dropped OpenCode from the slice, but the token still gets its own branch so the refusal names the
///     deferral instead of reading like a typo.
/// </remarks>
public static class UtilitiesStatusLineTargetCatalog
{
    /// <summary>The dropped OpenCode token, named only so its refusal can carry a deferral hint (D9).</summary>
    private const string OpenCodeToken = "opencode";

    /// <summary>The follow-up issue that records OpenCode support (D9, T15).</summary>
    private const string OpenCodeDeferralUrl = "https://github.com/dpalfery/kyber-weave/issues/165";

    /// <summary>Every target this branch deploys, in a stable order.</summary>
    public static IReadOnlyList<StatusLineTarget> All =>
    [
        StatusLineTarget.Claude,
        StatusLineTarget.Agy,
        StatusLineTarget.Pi
    ];

    /// <summary>The lowercase CLI token for <paramref name="target" />.</summary>
    public static string GetToken(StatusLineTarget target)
    {
        return target switch
        {
            StatusLineTarget.Claude => "claude",
            StatusLineTarget.Agy => "agy",
            StatusLineTarget.Pi => "pi",
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "No status-line token exists for this target.")
        };
    }

    /// <summary>The comma-separated list of valid tokens, for an error hint.</summary>
    public static string DescribeValidTokens()
    {
        return string.Join(", ", All.Select(GetToken));
    }

    /// <summary>
    ///     Resolves a <c>--target</c> selection. An absent value selects all three harnesses; an
    ///     unrecognized token is a client-input error, and <c>opencode</c> additionally points at the
    ///     follow-up issue that carries its deferral.
    /// </summary>
    /// <param name="token">The raw <c>--target</c> value, or <see langword="null" /> when omitted.</param>
    /// <returns>The targets the operator asked for, never empty.</returns>
    /// <exception cref="ArgumentException">The token names no supported harness.</exception>
    public static IReadOnlyList<StatusLineTarget> ParseSelection(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return All;

        string normalized = token.Trim();
        if (string.Equals(normalized, OpenCodeToken, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"'{OpenCodeToken}' is not a supported target. OpenCode support is deferred to a " +
                $"follow-up issue ({OpenCodeDeferralUrl}); this slice deploys claude, agy, and pi only.");

        foreach (StatusLineTarget target in All)
            if (string.Equals(GetToken(target), normalized, StringComparison.OrdinalIgnoreCase))
                return [target];

        throw new ArgumentException(
            $"'{normalized}' is not a supported target. Supported targets: {DescribeValidTokens()}.");
    }
}
