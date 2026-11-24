using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Domain.Tests.Contracts;

/// <summary>
/// Reflection and contract shape tests verifying <c>D11</c> placement of <see cref="CollectedBatch"/>,
/// unsealed contract types, and strict static typing across the four relational store interfaces in
/// <see cref="KyberDash.Contracts.Repositories"/>.
/// </summary>
public class StoreInterfaceShapeTests
{
    private static readonly Type[] StoreInterfaces =
    [
        typeof(IRelationalStoreWriter),
        typeof(IRelationalStoreReader),
        typeof(IProjectionStore),
        typeof(IOperationalStore),
    ];

    [Fact]
    public void CollectedTypes_LiveInKyberDashDomainAssembly_PreservingCollectorNamespace()
    {
        Assembly domainAssembly = typeof(RelationalSchemaCatalog).Assembly;
        Assembly contractsAssembly = typeof(IRelationalStoreWriter).Assembly;

        Assert.Equal(domainAssembly, typeof(CollectedBatch).Assembly);
        Assert.Equal("KyberDash.Data.Collector", typeof(CollectedBatch).Namespace);

        IReadOnlyList<string> contractReferences = contractsAssembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty)
            .ToList();
        Assert.DoesNotContain("KyberDash.Data", contractReferences, StringComparer.Ordinal);

        IReadOnlyList<string> domainReferences = domainAssembly
            .GetReferencedAssemblies()
            .Select(static name => name.Name ?? string.Empty)
            .ToList();
        Assert.DoesNotContain("KyberDash.Data", domainReferences, StringComparer.Ordinal);
        Assert.DoesNotContain("KyberDash.Contracts", domainReferences, StringComparer.Ordinal);
    }

    [Fact]
    public void LegacyTableReplacementEntry_AndAllStoreContractTypes_AreUnsealed()
    {
        Assert.False(
            typeof(LegacyTableReplacementEntry).IsSealed,
            "LegacyTableReplacementEntry must be unsealed per ArchitectureBoundaryTests.");

        HashSet<Type> visited = [];
        foreach (Type iface in StoreInterfaces)
        {
            Assert.True(iface.IsInterface, $"Expected '{iface.FullName}' to be an interface.");
            Assert.True(iface.IsPublic, $"Expected '{iface.FullName}' to be public.");
            Assert.Equal("KyberDash.Contracts.Repositories", iface.Namespace);

            foreach (MethodInfo method in iface.GetMethods())
            {
                CollectReachableTypes(method.ReturnType, visited);
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    CollectReachableTypes(parameter.ParameterType, visited);
                }
            }
        }

        foreach (Type type in visited)
        {
            if (type.Namespace is null || !type.Namespace.StartsWith("KyberDash.", StringComparison.Ordinal))
                continue;

            if (!type.IsClass || type.IsValueType || type.IsEnum || type.IsInterface)
                continue;

            Assert.False(
                type.IsSealed,
                $"Public KyberDash contract/model type '{type.FullName}' must remain unsealed.");
        }
    }

    [Fact]
    public void RetiredSessionDetailReadsAreGoneFromTheContracts()
    {
        // The monolithic detail read retired with its route: no reader member, no service
        // member, and no figure record carries the name — the section routes serve those fields.
        Assert.Null(typeof(IRelationalStoreReader).GetMethod("SessionDetailFigures", [typeof(string), typeof(IModelPricingLookup)]));
        Assert.Null(typeof(IDashboardQueryService).GetMethod("GetSessionDetail", [typeof(string)]));
        Assert.Null(
            typeof(IRelationalStoreReader).Assembly.GetType(
                "KyberDash.Contracts.Repositories.SessionDetailFigure",
                throwOnError: false));
    }

    [Fact]
    public void RelationalStoreInterfaces_ExposeRequiredTypedOperations()
    {
        AssertHasMethods(
            typeof(IRelationalStoreWriter),
            [
                nameof(IRelationalStoreWriter.WriteBatch),
                nameof(IRelationalStoreWriter.DeleteSource),
                nameof(IRelationalStoreWriter.WriteProblem),
            ]);

        MethodInfo? writeBatch = typeof(IRelationalStoreWriter).GetMethod(nameof(IRelationalStoreWriter.WriteBatch));
        Assert.NotNull(writeBatch);
        Assert.Equal([typeof(CollectedBatch)], writeBatch.GetParameters().Select(static p => p.ParameterType).ToArray());

        MethodInfo? deleteSource = typeof(IRelationalStoreWriter).GetMethod(nameof(IRelationalStoreWriter.DeleteSource));
        Assert.NotNull(deleteSource);
        Assert.Equal([typeof(string)], deleteSource.GetParameters().Select(static p => p.ParameterType).ToArray());

        MethodInfo? writeProblem = typeof(IRelationalStoreWriter).GetMethod(nameof(IRelationalStoreWriter.WriteProblem));
        Assert.NotNull(writeProblem);
        Assert.Equal([typeof(CollectedProblem)], writeProblem.GetParameters().Select(static p => p.ParameterType).ToArray());

        AssertHasMethods(
            typeof(IRelationalStoreReader),
            [
                nameof(IRelationalStoreReader.TokenCounts),
                nameof(IRelationalStoreReader.ReportedTotals),
                nameof(IRelationalStoreReader.ReportedCosts),
                nameof(IRelationalStoreReader.SkillsInvoked),
                nameof(IRelationalStoreReader.ToolCalls),
                nameof(IRelationalStoreReader.ToolDefinitions),
                nameof(IRelationalStoreReader.AgentInvocations),
                nameof(IRelationalStoreReader.RecordSignals),
                nameof(IRelationalStoreReader.Sessions),
                nameof(IRelationalStoreReader.StoredFieldsInventory),
                nameof(IRelationalStoreReader.StoredFieldsRows),
                nameof(IRelationalStoreReader.SessionListFigures),
                nameof(IRelationalStoreReader.SessionSummaryFigures),
                nameof(IRelationalStoreReader.SessionReportFigures),
                nameof(IRelationalStoreReader.TurnFigures),
                nameof(IRelationalStoreReader.RecordContentFree),
                nameof(IRelationalStoreReader.SessionRecordsContentFree),
            ]);

        MethodInfo? recordContentFree = typeof(IRelationalStoreReader).GetMethod(nameof(IRelationalStoreReader.RecordContentFree));
        Assert.NotNull(recordContentFree);
        Assert.Equal(typeof(CollectedRecord), recordContentFree.ReturnType);
        Assert.Equal([typeof(string)], recordContentFree.GetParameters().Select(static p => p.ParameterType).ToArray());

        MethodInfo? sessionRecordsContentFree = typeof(IRelationalStoreReader).GetMethod(nameof(IRelationalStoreReader.SessionRecordsContentFree));
        Assert.NotNull(sessionRecordsContentFree);
        Assert.Equal(typeof(IReadOnlyList<CollectedRecord>), sessionRecordsContentFree.ReturnType);
        Assert.Equal([typeof(string)], sessionRecordsContentFree.GetParameters().Select(static p => p.ParameterType).ToArray());

        MethodInfo? storedFieldsInventory = typeof(IRelationalStoreReader).GetMethod(nameof(IRelationalStoreReader.StoredFieldsInventory));
        Assert.NotNull(storedFieldsInventory);
        Assert.Equal(typeof(StoredFieldsInventoryView), storedFieldsInventory.ReturnType);
        Assert.Equal([typeof(string)], storedFieldsInventory.GetParameters().Select(static p => p.ParameterType).ToArray());

        MethodInfo? storedFieldsRows = typeof(IRelationalStoreReader).GetMethod(nameof(IRelationalStoreReader.StoredFieldsRows));
        Assert.NotNull(storedFieldsRows);
        Assert.Equal(typeof(StoredTableRows), storedFieldsRows.ReturnType);
        Assert.Equal(
            [typeof(string), typeof(string), typeof(int), typeof(int)],
            storedFieldsRows.GetParameters().Select(static p => p.ParameterType).ToArray());

        Assert.True(typeof(IModelPricingLookup).IsInterface);
        Assert.True(typeof(IModelPricingLookup).IsPublic);
        Assert.True(typeof(IModelPricingLookup).IsAssignableFrom(typeof(PricingTables)));
        Assert.False(typeof(PricingTables).IsSealed);

        AssertHasMethods(
            typeof(IProjectionStore),
            [
                nameof(IProjectionStore.GetSessionProjection),
                nameof(IProjectionStore.ListSessionProjections),
                nameof(IProjectionStore.ReplaceSessionProjection),
                nameof(IProjectionStore.ReplaceSessionProjections),
                nameof(IProjectionStore.DeleteSessionProjection),
                nameof(IProjectionStore.GetSessionTurns),
                nameof(IProjectionStore.ReplaceSessionTurns),
                nameof(IProjectionStore.GetRun),
                nameof(IProjectionStore.ListRuns),
                nameof(IProjectionStore.ReplaceRuns),
                nameof(IProjectionStore.DeleteRun),
                nameof(IProjectionStore.GetExecution),
                nameof(IProjectionStore.ListExecutionsForRun),
                nameof(IProjectionStore.ListExecutionsByHarness),
                nameof(IProjectionStore.ReplaceExecutions),
                nameof(IProjectionStore.DeleteExecutionsForRun),
                nameof(IProjectionStore.GetHarnessRollup),
                nameof(IProjectionStore.ListHarnessRollups),
                nameof(IProjectionStore.ReplaceHarnessRollups),
                nameof(IProjectionStore.DeleteHarnessRollup),
                nameof(IProjectionStore.GetFinding),
                nameof(IProjectionStore.ListFindings),
                nameof(IProjectionStore.ReplaceFindings),
                nameof(IProjectionStore.DeleteFindingsForSession),
                nameof(IProjectionStore.GetPrediction),
                nameof(IProjectionStore.ListPredictions),
                nameof(IProjectionStore.ReplacePrediction),
                nameof(IProjectionStore.DeletePrediction),
            ]);

        AssertHasMethods(
            typeof(IOperationalStore),
            [
                nameof(IOperationalStore.ReadSettings),
                nameof(IOperationalStore.SaveSettings),
                nameof(IOperationalStore.GetStoreState),
                nameof(IOperationalStore.SetStoreState),
                nameof(IOperationalStore.GetWriteMarker),
                nameof(IOperationalStore.SetWriteMarker),
                nameof(IOperationalStore.GetLastCleanAtUtc),
                nameof(IOperationalStore.SetLastCleanAtUtc),
                nameof(IOperationalStore.GetProjectionState),
                nameof(IOperationalStore.SetProjectionState),
                nameof(IOperationalStore.GetProjectionDirtyState),
                nameof(IOperationalStore.SetProjectionDirtyState),
                nameof(IOperationalStore.ClearProjectionDirtyState),
                nameof(IOperationalStore.ListSessionKeyAliases),
                nameof(IOperationalStore.ReplaceSessionKeyAliases),
                nameof(IOperationalStore.UpsertRefreshRun),
                nameof(IOperationalStore.GetRefreshRun),
                nameof(IOperationalStore.ListRefreshRuns),
                nameof(IOperationalStore.GetSourceCheckpoint),
                nameof(IOperationalStore.UpsertSourceCheckpoint),
                nameof(IOperationalStore.ListSourceCheckpoints),
                nameof(IOperationalStore.GetIngestProblem),
                nameof(IOperationalStore.ListIngestProblems),
                nameof(IOperationalStore.CountIngestProblems),
                nameof(IOperationalStore.PutQuarantinedSpan),
                nameof(IOperationalStore.GetQuarantinedSpan),
                nameof(IOperationalStore.ListQuarantinedSpans),
                nameof(IOperationalStore.CountQuarantinedSpans),
                nameof(IOperationalStore.GetCachedTokenCount),
                nameof(IOperationalStore.PutCachedTokenCounts),
                nameof(IOperationalStore.GetModelContextWindowCatalog),
                nameof(IOperationalStore.ReplaceModelContextWindowCatalog),
                nameof(IOperationalStore.PutMetricPoints),
                nameof(IOperationalStore.ListMetricPoints),
            ]);

        MethodInfo? readSettings = typeof(IOperationalStore).GetMethod(nameof(IOperationalStore.ReadSettings));
        Assert.NotNull(readSettings);
        Assert.Equal(typeof(SettingsView), readSettings.ReturnType);
    }

    /// <summary>
    /// The reader contract carries no broad read: the whole-record, whole-content,
    /// whole-aggregate, and whole-turn-summary members the request paths migrated off are gone
    /// from the interface and from every implementer of it the test host has loaded, so no new
    /// consumer can reach the read that stalls on a large store.
    /// </summary>
    [Fact]
    public void TheReaderInterfaceCarriesNoBroadReads()
    {
        string[] retired =
        [
            "Records",
            "GetRecord",
            "ContentParts",
            "GetSessionAggregate",
            "ListSessionAggregates",
            "SessionTurnSummaries",
        ];

        foreach (Type reader in ReaderContractTypes())
        {
            HashSet<string> declared =
            [
                .. reader.GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(static method => method.Name),
            ];

            foreach (string name in retired)
            {
                Assert.False(
                    declared.Contains(name),
                    $"{reader.FullName} still declares the retired broad read '{name}'.");
            }
        }
    }

    /// <summary>
    /// The reader interface itself plus every concrete implementer the test host has loaded —
    /// the linked <see cref="InMemoryRelationalStore"/> and any other implementer a referenced
    /// assembly brings in.
    /// </summary>
    private static IEnumerable<Type> ReaderContractTypes()
    {
        yield return typeof(IRelationalStoreReader);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            foreach (Type type in LoadedTypes(assembly))
            {
                if (type is { IsClass: true, IsAbstract: false } && typeof(IRelationalStoreReader).IsAssignableFrom(type))
                {
                    yield return type;
                }
            }
        }
    }

    private static IEnumerable<Type> LoadedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static type => type is not null).Select(static type => type!);
        }
    }

    [Fact]
    public void StoreInterfaces_AndReachableModels_ContainNoJsonElementJsonNodeObjectOrDictionarySignatures()
    {
        HashSet<Type> visited = [];

        foreach (Type iface in StoreInterfaces)
        {
            foreach (MethodInfo method in iface.GetMethods())
            {
                string memberLocation = $"{iface.Name}.{method.Name}(return)";
                AssertStaticTypedSignature(memberLocation, method.ReturnType, visited);

                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    string paramLocation = $"{iface.Name}.{method.Name}({parameter.Name})";
                    AssertStaticTypedSignature(paramLocation, parameter.ParameterType, visited);
                }
            }
        }
    }

    [Fact]
    public void StoredFieldsInventoryView_ModelsCatalogTableEntriesCountsAndMissingParentKeys()
    {
        StoredTableInventory table = new(
            TableName: "record_model",
            ColumnNames: ["span_id", "model_id"],
            RowCount: 0,
            MissingParentKeys: ["span-2"]);

        StoredFieldsInventoryView view = new("session-1", [table]);

        Assert.Equal("session-1", view.SessionKey);
        Assert.Single(view.Tables);
        Assert.Equal("record_model", view.Tables[0].TableName);
        Assert.Equal(["span_id", "model_id"], view.Tables[0].ColumnNames);
        Assert.Equal(0, view.Tables[0].RowCount);
        Assert.Equal(["span-2"], view.Tables[0].MissingParentKeys);
    }

    [Fact]
    public void StoredTableRows_ModelsOnePagedTableOfRowValues()
    {
        StoredTableRows rows = new(
            TableName: "content_part_text",
            ColumnNames: ["span_id", "position", "part_text"],
            TotalRows: 30,
            Page: 1,
            Limit: 25,
            Offset: 0,
            Rows:
            [
                new StoredFieldRow(
                    [
                        new StoredFieldCell("span_id", SqliteColumnType.SqliteText, TextValue: "span-1"),
                        new StoredFieldCell("position", SqliteColumnType.SqliteInteger, IntegerValue: 0),
                        new StoredFieldCell("part_text", SqliteColumnType.SqliteText, TextValue: "hello"),
                    ]),
            ]);

        Assert.Equal("content_part_text", rows.TableName);
        Assert.Equal(30, rows.TotalRows);
        Assert.Equal(1, rows.Page);
        Assert.Equal(25, rows.Limit);
        Assert.Equal(0, rows.Offset);
        Assert.Single(rows.Rows);
        Assert.Equal("hello", rows.Rows[0].Cells[2].TextValue);
    }

    private static void AssertHasMethods(Type interfaceType, string[] expectedMethodNames)
    {
        HashSet<string> actual = interfaceType
            .GetMethods()
            .Select(static m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string expected in expectedMethodNames)
        {
            Assert.Contains(expected, actual);
        }
    }

    private static void AssertStaticTypedSignature(string location, Type type, HashSet<Type> visited)
    {
        Type unwrapped = Nullable.GetUnderlyingType(type) ?? type;

        Assert.False(
            unwrapped == typeof(object),
            $"Signature '{location}' must not expose object.");

        Assert.False(
            unwrapped == typeof(JsonElement) || unwrapped == typeof(JsonDocument),
            $"Signature '{location}' must not expose {unwrapped.Name}.");

        Assert.False(
            typeof(JsonNode).IsAssignableFrom(unwrapped),
            $"Signature '{location}' must not expose JsonNode ({unwrapped.Name}).");

        Assert.False(
            IsDictionaryType(unwrapped),
            $"Signature '{location}' must not expose a dictionary ({unwrapped.Name}).");

        if (unwrapped.IsArray)
        {
            Type? elementType = unwrapped.GetElementType();
            if (elementType is not null)
                AssertStaticTypedSignature($"{location}[]", elementType, visited);
            return;
        }

        if (unwrapped.IsGenericType)
        {
            foreach (Type genericArgument in unwrapped.GetGenericArguments())
            {
                AssertStaticTypedSignature($"{location}<{genericArgument.Name}>", genericArgument, visited);
            }
        }

        if (unwrapped.Namespace is null || !unwrapped.Namespace.StartsWith("KyberDash.", StringComparison.Ordinal))
            return;

        if (unwrapped.IsEnum || !visited.Add(unwrapped))
            return;

        foreach (PropertyInfo property in unwrapped.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (string.Equals(property.Name, "EqualityContract", StringComparison.Ordinal))
                continue;

            AssertStaticTypedSignature($"{unwrapped.Name}.{property.Name}", property.PropertyType, visited);
        }
    }

    private static void CollectReachableTypes(Type type, HashSet<Type> visited)
    {
        Type unwrapped = Nullable.GetUnderlyingType(type) ?? type;

        if (unwrapped.IsArray)
        {
            Type? elementType = unwrapped.GetElementType();
            if (elementType is not null)
                CollectReachableTypes(elementType, visited);
            return;
        }

        if (unwrapped.IsGenericType)
        {
            foreach (Type genericArgument in unwrapped.GetGenericArguments())
            {
                CollectReachableTypes(genericArgument, visited);
            }
        }

        if (unwrapped.Namespace is null || !unwrapped.Namespace.StartsWith("KyberDash.", StringComparison.Ordinal))
            return;

        if (!visited.Add(unwrapped))
            return;

        foreach (PropertyInfo property in unwrapped.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (string.Equals(property.Name, "EqualityContract", StringComparison.Ordinal))
                continue;

            CollectReachableTypes(property.PropertyType, visited);
        }
    }

    private static bool IsDictionaryType(Type type)
    {
        if (typeof(IDictionary).IsAssignableFrom(type))
            return true;

        if (type.IsGenericType)
        {
            Type genericDefinition = type.GetGenericTypeDefinition();
            if (genericDefinition == typeof(IDictionary<,>) || genericDefinition == typeof(IReadOnlyDictionary<,>))
                return true;
        }

        return type.GetInterfaces().Any(static iface =>
            iface.IsGenericType
            && (iface.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                || iface.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
    }
}
