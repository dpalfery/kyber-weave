using KyberWeave.Arbiter.Hooks;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// What an event can tell about its own prompt. It lives here rather than on <see cref="ArbiterEvent"/>
/// so the record stays data.
/// </summary>
public static class ArbiterEventExtensions
{
    /// <summary>
    /// Whether this event's prompt is one a dispatch hook inserted. A return event carries the input the harness
    /// executed, so an expanded prompt there is that proof. A pre event carries the conductor's own text, where an
    /// expanded-looking prompt only means the conductor typed the tags, so it is never trusted.
    /// </summary>
    /// <param name="arbiterEvent">The event to judge.</param>
    /// <returns>True when a return event's prompt carries the tags the hook inserts.</returns>
    public static bool PromptInserted(this ArbiterEvent arbiterEvent)
    {
        ArgumentNullException.ThrowIfNull(arbiterEvent);

        return arbiterEvent.Phase is "post" or "return" && DispatchPromptInsertion.IsExpanded(arbiterEvent.Prompt);
    }
}
