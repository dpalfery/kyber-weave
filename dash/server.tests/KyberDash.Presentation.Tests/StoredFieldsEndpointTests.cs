using System.Net;
using System.Text.Json;
using KyberDash.Api;
using KyberDash.Contracts.Repositories;
using KyberDash.Domain.Schema;
using Microsoft.AspNetCore.Builder;
using Moq;
using Xunit;

namespace KyberDash.Presentation.Tests;

/// <summary>
/// Drives the catalog-driven stored-fields endpoint through the real Minimal API map, substituting
/// <c>IDashboardQueryService</c> with a mock. These assert the wire contract: the
/// <c>/api/kyber/session/{sessionKey}/stored-fields</c> route (never <c>/api/sessions/...</c>),
/// every reachable catalog column labeled <c>table.column</c>, absent optional rows marked not
/// reported, and HTTP 404 for an unknown session.
/// </summary>
public class StoredFieldsEndpointTests
{
    private const string SessionKey = "copilot-session-1";

    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task StoredFieldsReturnsEveryReachableCatalogColumnLabeledTableColumn()
    {
        StoredFieldsView view = CopilotView(SessionKey);
        await using Host host = await Host.StartAsync(SessionKey, view);

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri($"/api/kyber/session/{SessionKey}/stored-fields", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        HashSet<string> labels = CollectLabels(body);
        foreach (StoredTableFields table in view.Tables)
        {
            foreach (string column in table.ColumnNames)
            {
                Assert.Contains($"{table.TableName}.{column}", labels);
            }
        }
    }

    [Fact]
    public async Task StoredFieldsMarksAbsentOptionalRowsAsNotReported()
    {
        StoredFieldsView view = CopilotView(SessionKey);
        await using Host host = await Host.StartAsync(SessionKey, view);

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri($"/api/kyber/session/{SessionKey}/stored-fields", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement tables = body.RootElement.GetProperty("tables");
        JsonElement recordModel = Assert.Single(
            tables.EnumerateArray(),
            static entry => string.Equals(
                entry.GetProperty("tableName").GetString(), "record_model", StringComparison.Ordinal));
        Assert.Equal(0, recordModel.GetProperty("rows").GetArrayLength());
        string[] missing = [.. recordModel.GetProperty("missingParentKeys").EnumerateArray()
            .Select(static key => key.GetString()!)];
        Assert.Contains("span-copilot-1", missing);
    }

    [Fact]
    public async Task StoredFieldsReturnsNotFoundForUnknownSession()
    {
        await using Host host = await Host.StartAsync(SessionKey, CopilotView(SessionKey));

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri("/api/kyber/session/unknown-session/stored-fields", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StoredFieldsIsNeverServedUnderApiSessions()
    {
        await using Host host = await Host.StartAsync(SessionKey, CopilotView(SessionKey));

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri($"/api/sessions/{SessionKey}/stored-fields", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Builds a Copilot-session view from the real catalog: populated <c>session</c> and
    /// <c>record</c> rows plus an empty optional <c>record_model</c> child whose absent row is
    /// reported through missing parent keys.
    /// </summary>
    private static StoredFieldsView CopilotView(string sessionKey)
    {
        TableDefinition sessionTable = RelationalSchemaCatalog.GetTable("session");
        TableDefinition recordTable = RelationalSchemaCatalog.GetTable("record");
        TableDefinition recordModelTable = RelationalSchemaCatalog.GetTable("record_model");

        return new StoredFieldsView(
            sessionKey,
            [
                new StoredTableFields(
                    sessionTable.Name,
                    [.. sessionTable.Columns.Select(static column => column.Name)],
                    [new StoredFieldRow([.. sessionTable.Columns.Select(column => SampleCell(column, sessionKey))])],
                    []),
                new StoredTableFields(
                    recordTable.Name,
                    [.. recordTable.Columns.Select(static column => column.Name)],
                    [new StoredFieldRow([.. recordTable.Columns.Select(column => SampleCell(column, sessionKey))])],
                    []),
                new StoredTableFields(
                    recordModelTable.Name,
                    [.. recordModelTable.Columns.Select(static column => column.Name)],
                    [],
                    ["span-copilot-1"]),
            ]);
    }

    /// <summary>Builds one sample cell carrying the column's own affinity.</summary>
    private static StoredFieldCell SampleCell(ColumnDefinition column, string sessionKey) =>
        column.Type switch
        {
            SqliteColumnType.SqliteText => new StoredFieldCell(column.Name, column.Type, TextValue: SampleText(column.Name, sessionKey)),
            SqliteColumnType.SqliteInteger => new StoredFieldCell(column.Name, column.Type, IntegerValue: 1),
            SqliteColumnType.SqliteReal => new StoredFieldCell(column.Name, column.Type, RealValue: 0.5),
            _ => throw new InvalidOperationException($"Unsupported catalog column type '{column.Type}'."),
        };

    /// <summary>Returns deterministic sample text honoring key-shaped columns.</summary>
    private static string SampleText(string columnName, string sessionKey) =>
        columnName switch
        {
            "session_id" => sessionKey,
            "session_key" => sessionKey,
            "span_id" => "span-copilot-1",
            _ => $"sample-{columnName}",
        };

    /// <summary>Collects every <c>table.column</c> label carried by the response.</summary>
    private static HashSet<string> CollectLabels(JsonDocument body)
    {
        HashSet<string> labels = new(StringComparer.Ordinal);
        foreach (JsonElement table in body.RootElement.GetProperty("tables").EnumerateArray())
        {
            string tableName = table.GetProperty("tableName").GetString()!;
            foreach (JsonElement column in table.GetProperty("columnNames").EnumerateArray())
            {
                labels.Add($"{tableName}.{column.GetString()}");
            }

            foreach (JsonElement row in table.GetProperty("rows").EnumerateArray())
            {
                foreach (JsonElement cell in row.GetProperty("cells").EnumerateArray())
                {
                    labels.Add($"{tableName}.{cell.GetProperty("columnName").GetString()}");
                }
            }
        }

        return labels;
    }

    /// <summary>Owns a started test application serving one canned stored-fields view.</summary>
    private class Host : IAsyncDisposable
    {
        private Host(WebApplication app, HttpClient client)
        {
            App = app;
            Client = client;
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public static async Task<Host> StartAsync(string sessionKey, StoredFieldsView view)
        {
            PresentationBuilder builder = new();
            builder.ApplyCannedResults();
            JsonElement payload = JsonSerializer.SerializeToElement(view, CamelCaseOptions);
            builder.Query
                .Setup(q => q.GetStoredFields(sessionKey))
                .Returns((JsonElement?)payload);
            builder.Query
                .Setup(q => q.GetStoredFields(It.Is<string>(key => !string.Equals(key, sessionKey, StringComparison.Ordinal))))
                .Returns((JsonElement?)null);

            WebApplication app = await PresentationTestHost.StartAsync(builder.AllSubstitutes(), builder.SettingsRuntime);
            return new Host(app, PresentationTestHost.CreateClient(app));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
