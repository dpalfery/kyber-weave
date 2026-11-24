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
/// Drives the stored-fields routes through the real Minimal API map, substituting
/// <c>IDashboardQueryService</c> with a mock. These assert the wire contract (decision D15): the
/// content-free inventory at <c>/api/kyber/session/{sessionKey}/stored-fields</c> (never
/// <c>/api/sessions/...</c>) carrying names, columns, counts and missing keys with no row values,
/// and the per-table rows route <c>/api/kyber/session/{sessionKey}/stored-fields/{table}</c>
/// answering one page of 25, with HTTP 404 for an unknown session or a table outside the catalog.
/// </summary>
public class StoredFieldsEndpointTests
{
    private const string SessionKey = "copilot-session-1";

    private static readonly JsonSerializerOptions CamelCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task StoredFieldsInventoryReturnsNamesColumnsCountsAndMissingKeysWithoutRowValues()
    {
        StoredFieldsInventoryView view = CopilotInventory(SessionKey);
        await using Host host = await Host.StartAsync(SessionKey, inventory: view);

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri($"/api/kyber/session/{SessionKey}/stored-fields", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        HashSet<string> labels = CollectColumnLabels(body);
        foreach (StoredTableInventory table in view.Tables)
        {
            foreach (string column in table.ColumnNames)
            {
                Assert.Contains($"{table.TableName}.{column}", labels);
            }
        }
    }

    [Fact]
    public async Task StoredFieldsInventoryCarriesNoRowCells()
    {
        await using Host host = await Host.StartAsync(SessionKey, inventory: CopilotInventory(SessionKey));

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri($"/api/kyber/session/{SessionKey}/stored-fields", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Content-free: counts and column names only — never a row array or a typed cell.
        string raw = body.RootElement.GetRawText();
        Assert.DoesNotContain("\"rows\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"cells\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"textValue\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredFieldsInventoryMarksAbsentOptionalRowsAsNotReported()
    {
        await using Host host = await Host.StartAsync(SessionKey, inventory: CopilotInventory(SessionKey));

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri($"/api/kyber/session/{SessionKey}/stored-fields", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement tables = body.RootElement.GetProperty("tables");
        JsonElement recordModel = Assert.Single(
            tables.EnumerateArray(),
            static entry => string.Equals(
                entry.GetProperty("tableName").GetString(), "record_model", StringComparison.Ordinal));
        Assert.Equal(0, recordModel.GetProperty("rowCount").GetInt64());
        string[] missing = [.. recordModel.GetProperty("missingParentKeys").EnumerateArray()
            .Select(static key => key.GetString()!)];
        Assert.Contains("span-copilot-1", missing);
    }

    [Fact]
    public async Task StoredFieldsInventoryReturnsNotFoundForUnknownSession()
    {
        await using Host host = await Host.StartAsync(SessionKey, inventory: CopilotInventory(SessionKey));

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri("/api/kyber/session/unknown-session/stored-fields", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StoredFieldsInventoryIsNeverServedUnderApiSessions()
    {
        await using Host host = await Host.StartAsync(SessionKey, inventory: CopilotInventory(SessionKey));

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri($"/api/sessions/{SessionKey}/stored-fields", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StoredFieldsRowsReturnTheNamedTablesPageOneOf25AtADefault()
    {
        StoredTableRows rows = CopilotRows("record_model");
        await using Host host = await Host.StartAsync(SessionKey, rows: rows);

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri($"/api/kyber/session/{SessionKey}/stored-fields/record_model", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("record_model", body.RootElement.GetProperty("tableName").GetString());
        host.Builder.Query.Verify(
            q => q.GetStoredFieldRows(SessionKey, "record_model", 1, 25),
            Times.Once);
    }

    [Fact]
    public async Task StoredFieldsRowsParseThePageWindow()
    {
        StoredTableRows rows = CopilotRows("record_model");
        await using Host host = await Host.StartAsync(SessionKey, rows: rows);

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri($"/api/kyber/session/{SessionKey}/stored-fields/record_model?page=2&limit=5", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        host.Builder.Query.Verify(
            q => q.GetStoredFieldRows(SessionKey, "record_model", 2, 5),
            Times.Once);
    }

    [Fact]
    public async Task StoredFieldsRowsReturnsNotFoundForUnknownSession()
    {
        await using Host host = await Host.StartAsync(SessionKey, rows: CopilotRows("record_model"));

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri("/api/kyber/session/unknown-session/stored-fields/record_model", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StoredFieldsRowsReturnsNotFoundForATableOutsideTheCatalog()
    {
        await using Host host = await Host.StartAsync(SessionKey, rows: CopilotRows("record_model"));

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri($"/api/kyber/session/{SessionKey}/stored-fields/sqlite_master", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StoredFieldsRowsAreNeverServedUnderApiSessions()
    {
        await using Host host = await Host.StartAsync(SessionKey, rows: CopilotRows("record_model"));

        HttpResponseMessage response = await host.Client.GetAsync(
            new Uri($"/api/sessions/{SessionKey}/stored-fields/record_model", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Builds a Copilot-session inventory from the real catalog: populated <c>session</c> and
    /// <c>record</c> counts plus an empty optional <c>record_model</c> child whose absent row is
    /// reported through missing parent keys, and no row value anywhere.
    /// </summary>
    private static StoredFieldsInventoryView CopilotInventory(string sessionKey)
    {
        TableDefinition sessionTable = RelationalSchemaCatalog.GetTable("session");
        TableDefinition recordTable = RelationalSchemaCatalog.GetTable("record");
        TableDefinition recordModelTable = RelationalSchemaCatalog.GetTable("record_model");

        return new StoredFieldsInventoryView(
            sessionKey,
            [
                new StoredTableInventory(
                    sessionTable.Name,
                    [.. sessionTable.Columns.Select(static column => column.Name)],
                    1,
                    []),
                new StoredTableInventory(
                    recordTable.Name,
                    [.. recordTable.Columns.Select(static column => column.Name)],
                    1,
                    []),
                new StoredTableInventory(
                    recordModelTable.Name,
                    [.. recordModelTable.Columns.Select(static column => column.Name)],
                    0,
                    ["span-copilot-1"]),
            ]);
    }

    /// <summary>Builds one canned rows page for a table, mirroring the route's wire shape.</summary>
    private static StoredTableRows CopilotRows(string tableName)
    {
        TableDefinition table = RelationalSchemaCatalog.GetTable(tableName);
        return new StoredTableRows(
            tableName,
            [.. table.Columns.Select(static column => column.Name)],
            TotalRows: 1,
            Page: 1,
            Limit: 25,
            Offset: 0,
            Rows:
            [
                new StoredFieldRow([.. table.Columns.Select(static column => new StoredFieldCell(column.Name, column.Type))]),
            ]);
    }

    /// <summary>Collects every <c>table.column</c> label the inventory's column names carry.</summary>
    private static HashSet<string> CollectColumnLabels(JsonDocument body)
    {
        HashSet<string> labels = new(StringComparer.Ordinal);
        foreach (JsonElement table in body.RootElement.GetProperty("tables").EnumerateArray())
        {
            string tableName = table.GetProperty("tableName").GetString()!;
            foreach (JsonElement column in table.GetProperty("columnNames").EnumerateArray())
            {
                labels.Add($"{tableName}.{column.GetString()}");
            }
        }

        return labels;
    }

    /// <summary>Owns a started test application serving one canned inventory or rows payload.</summary>
    private class Host : IAsyncDisposable
    {
        private Host(WebApplication app, HttpClient client, PresentationBuilder builder)
        {
            App = app;
            Client = client;
            Builder = builder;
        }

        public WebApplication App { get; }

        public HttpClient Client { get; }

        public PresentationBuilder Builder { get; }

        public static async Task<Host> StartAsync(string sessionKey, StoredFieldsInventoryView? inventory = null, StoredTableRows? rows = null)
        {
            PresentationBuilder builder = new();
            builder.ApplyCannedResults();
            if (inventory is not null)
            {
                JsonElement payload = JsonSerializer.SerializeToElement(inventory, CamelCaseOptions);
                builder.Query
                    .Setup(q => q.GetStoredFieldsInventory(sessionKey))
                    .Returns((JsonElement?)payload);
            }

            if (rows is not null)
            {
                JsonElement payload = JsonSerializer.SerializeToElement(rows, CamelCaseOptions);
                builder.Query
                    .Setup(q => q.GetStoredFieldRows(
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<int>(),
                        It.IsAny<int>()))
                    .Returns((JsonElement?)null);
                builder.Query
                    .Setup(q => q.GetStoredFieldRows(sessionKey, rows.TableName, It.IsAny<int>(), It.IsAny<int>()))
                    .Returns((JsonElement?)payload);
            }

            if (inventory is null)
            {
                builder.Query
                    .Setup(q => q.GetStoredFieldsInventory(It.Is<string>(key => !string.Equals(key, sessionKey, StringComparison.Ordinal))))
                    .Returns((JsonElement?)null);
            }

            WebApplication app = await PresentationTestHost.StartAsync(builder.AllSubstitutes(), builder.SettingsRuntime);
            return new Host(app, PresentationTestHost.CreateClient(app), builder);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}
