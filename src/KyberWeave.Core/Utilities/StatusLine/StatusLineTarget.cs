namespace KyberWeave.Core.Utilities.StatusLine;

/// <summary>A harness whose status line Kyber Utilities deploys a variant for.</summary>
/// <remarks>
/// A closed vocabulary, mirroring <c>SquadTarget</c>: the three harnesses this slice covers (D1, D9).
/// Adding a member is a product change, not an authoring convenience — it moves together with the
/// per-harness root rules in <see cref="StatusLineTargetRoots"/>, the artifact set under
/// <c>products/kyber-utilities/statusline/</c>, and the activation guidance the CLI prints.
/// </remarks>
public enum StatusLineTarget
{
    /// <summary>Claude Code, activated through <c>statusLine</c> in the user's Claude settings.</summary>
    Claude,

    /// <summary>The Antigravity CLI (<c>agy</c>), activated through <c>statusLine</c> in its settings.</summary>
    Agy,

    /// <summary>Pi, activated through an <c>extensions</c> entry in its <c>settings.json</c>.</summary>
    Pi
}
