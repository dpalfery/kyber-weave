namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>One dispatch inside an <see cref="ArbiterEvent"/>: its own target and prompt.</summary>
/// <param name="Target">The dispatch target, when there is one.</param>
/// <param name="Prompt">The dispatch prompt, header block included.</param>
public sealed record ArbiterDispatch(string? Target, string Prompt);

/// <summary>
/// A harness-neutral Arbiter event: the harness token, phase, caller, target,
/// prompt, tool-call id, session, <c>cwd</c> and tool output. An event may carry
/// several dispatches, each with its own target and prompt, because Antigravity's
/// <c>invoke_subagent</c> launches a list in one call.
/// </summary>
/// <remarks>
/// The caller fields mirror the resolution order: the harness payload outranks the
/// rendered <c>--caller</c>, which outranks header inference. <see cref="Caller"/>
/// with <see cref="CallerSource"/> carries the strongest source that named anyone;
/// the three explicit sources let the classifier re-resolve without guessing which
/// field <see cref="Caller"/> came from.
/// </remarks>
public sealed record ArbiterEvent
{
    /// <summary>The harness token (for example <c>claude</c> or <c>antigravity</c>).</summary>
    public string Harness { get; init; } = string.Empty;

    /// <summary>The event phase: <c>pre</c>, <c>post</c> or <c>return</c>.</summary>
    public string Phase { get; init; } = "pre";

    /// <summary>The caller named by the strongest source, or null when unidentified.</summary>
    public string? Caller { get; init; }

    /// <summary>How <see cref="Caller"/> was named (see <see cref="ArbiterCallerSources"/>).</summary>
    public string? CallerSource { get; init; }

    /// <summary>The caller named by the harness payload, when it carries one.</summary>
    public string? HarnessCaller { get; init; }

    /// <summary>The caller named by the rendered <c>--caller</c>, when one fired.</summary>
    public string? RenderedCaller { get; init; }

    /// <summary>The caller asserted by <c>serve</c>, which names its own identity.</summary>
    public string? AssertedCaller { get; init; }

    /// <summary>The dispatch target, when there is one.</summary>
    public string? Target { get; init; }

    /// <summary>The dispatch prompt, header block included.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>The harness tool-call id, where the payload carries one.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>The payload's session id, when it has one.</summary>
    public string? Session { get; init; }

    /// <summary>The working directory the dispatch runs in.</summary>
    public string? Cwd { get; init; }

    /// <summary>
    /// Whether the serving harness observes sub-agent returns (task 16.9). The hook
    /// host copies it from the harness adapter so <c>harness.observes-returns</c> is
    /// always supplied: true for every harness, false only where the adapter declares
    /// returns unobservable. Defaults to true — returns observed — so paths without a
    /// harness hook keep every rule enforced.
    /// </summary>
    public bool ObservesReturns { get; init; } = true;

    /// <summary>The tool output, on post-dispatch (return) events.</summary>
    public string? ToolOutput { get; init; }

    /// <summary>Whether the event is a dispatch. A non-dispatch passes with no trigger.</summary>
    public bool IsDispatch { get; init; } = true;

    /// <summary>
    /// The dispatches of a multi-dispatch event, each with its own target and prompt.
    /// Empty for a single-dispatch event, which uses <see cref="Target"/> and
    /// <see cref="Prompt"/> instead.
    /// </summary>
    public IReadOnlyList<ArbiterDispatch> Dispatches { get; init; } = [];
}
