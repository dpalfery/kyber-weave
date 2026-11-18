namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The schema identifiers and the unanchored marker the Squad lock and receipt files carry.</summary>
public static class SquadStateSchemas
{
    /// <summary>Schema identifier for Squad lock v1 (unmodified portable locks without model presets).</summary>
    public const string LockSchemaV1 = "kyber-squad.lock/v1";

    /// <summary>Schema identifier for Squad lock v2 (recording active model presets per target).</summary>
    public const string LockSchemaV2 = "kyber-squad.lock/v2";

    /// <summary>
    ///     The receipt <c>targetRoot</c> a plan records before the transaction anchors it. Global
    ///     receipts are anchored on the home, which only the state store knows, so the planner writes this
    ///     portable marker and the transaction replaces it at write time. Legacy receipts carry it too.
    /// </summary>
    public const string UnanchoredTargetRoot = ".";

    /// <summary>
    ///     The one receipt schema source (#91 / A3): project receipts stay on this schema forever,
    ///     and it is also what a legacy Global receipt written before #91 carries.
    /// </summary>
    public const string ReceiptSchemaV1 = "kyber-squad.receipt/v1";

    /// <summary>
    ///     Written for Global receipts only, additive over v1 with a required <c>layout</c> field.
    ///     An older CLI that predates this schema refuses it outright rather than misreading it.
    /// </summary>
    public const string ReceiptSchemaV2 = "kyber-squad.receipt/v2";

    /// <summary>
    /// Written for project receipts that own at least one hook-file block, additive over the
    /// v1 project field set with a required <c>blocks</c> field. Receipts without blocks
    /// stay byte-identical v1 or v2, as ADR 0024 requires. An older CLI that predates this
    /// schema refuses it outright rather than misreading it.
    /// </summary>
    public const string ReceiptSchemaV3 = "kyber-squad.receipt/v3";
}
