namespace KyberDash.Data.Collector;

/// <summary>
/// Which record token values a parser contract version stored as estimates for one file source
/// (<c>record_token_count</c> and <c>record_reported_total</c> rows of the source's records).
/// </summary>
/// <remarks>
/// KyberDash never estimates (decision D15). Readers that once split or guessed token counts had
/// their parser contract version raised, so a re-read replaces those rows; a unit whose file is gone
/// cannot be re-read, and this names the rows to clear to "not measured" instead.
/// </remarks>
public enum EstimatedRecordTokens
{
    /// <summary>The version stored no estimated token values.</summary>
    None,

    /// <summary>
    /// Every token count and every reported total of the source's records was an estimate, as with
    /// a session or conversation total split across its calls.
    /// </summary>
    AllRecordTokens,

    /// <summary>
    /// A record's token counts were estimates exactly when the record carries no reported total:
    /// a record with a reported total holds the counts the harness reported.
    /// </summary>
    RecordsWithoutReportedTotal,
}
