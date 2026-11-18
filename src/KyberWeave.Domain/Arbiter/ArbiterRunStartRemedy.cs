namespace KyberWeave.Core.Arbiter;

/// <summary>The fix an escalation names when a dispatch is refused for want of a started run.</summary>
public static class ArbiterRunStartRemedy
{
    /// <summary>The fix a refused dispatch names.</summary>
    public const string Text =
        "start the run first: kyber-weave run start --harness <harness> --type <type> --slug <slug> " +
        "(or dispatch devops with RUN-START: true and that command), then send this dispatch again.";
}
