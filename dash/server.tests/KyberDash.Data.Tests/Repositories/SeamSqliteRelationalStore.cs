using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KyberDash.Core.Resilience;
using KyberDash.Data.Repositories;
using Microsoft.Data.Sqlite;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// A <see cref="SqliteRelationalStore"/> subclass that records every parameterized read handed to
/// the <c>QueryRows</c> seam — with each bound parameter rendered as a SQL literal — so a test can
/// assert which reads a code path issued and how each one is scoped. The keyed-read and
/// one-statement assertions behind the sessions, findings, and sectioned pages rely on it.
/// </summary>
public class SeamSqliteRelationalStore : SqliteRelationalStore
{
    private readonly List<string> executedSql = [];

    /// <summary>Creates a recording store over a new database file.</summary>
    /// <param name="databasePath">Database file path.</param>
    /// <param name="circuitBreaker">Circuit breaker guarding execution.</param>
    public SeamSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
        : base(databasePath, circuitBreaker)
    {
    }

    /// <summary>Every read since <see cref="ClearRecordedSql"/> was last called, parameters rendered.</summary>
    public IReadOnlyList<string> ExecutedSql => executedSql;

    /// <summary>Clears the recorded scripts.</summary>
    public void ClearRecordedSql() => executedSql.Clear();

    /// <inheritdoc />
    protected override IReadOnlyList<T> QueryRows<T>(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        Func<SqliteDataReader, T> map)
    {
        executedSql.Add(RenderParameters(sql, parameters));
        return base.QueryRows(sql, parameters, map);
    }

    /// <summary>Renders each bound marker as a SQL literal so a test can assert on the scope values.</summary>
    /// <param name="sql">SQL text with <c>$name</c> markers.</param>
    /// <param name="parameters">Parameter values keyed by marker.</param>
    /// <returns>The SQL text with every marker replaced by its literal.</returns>
    protected static string RenderParameters(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        string rendered = sql;
        foreach (KeyValuePair<string, object?> parameter in parameters.OrderByDescending(static parameter => parameter.Key.Length))
        {
            rendered = rendered.Replace(parameter.Key, RenderLiteral(parameter.Value), StringComparison.Ordinal);
        }

        return rendered;
    }

    private static string RenderLiteral(object? value) => value switch
    {
        null => "NULL",
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NULL",
    };
}
