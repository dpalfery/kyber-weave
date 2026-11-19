using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberDash.Contracts.Repositories;
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
                nameof(IRelationalStoreReader.Records),
                nameof(IRelationalStoreReader.TokenCounts),
                nameof(IRelationalStoreReader.ReportedTotals),
                nameof(IRelationalStoreReader.ReportedCosts),
                nameof(IRelationalStoreReader.SkillsInvoked),
                nameof(IRelationalStoreReader.ToolCalls),
                nameof(IRelationalStoreReader.ContentParts),
                nameof(IRelationalStoreReader.ToolDefinitions),
                nameof(IRelationalStoreReader.AgentInvocations),
                nameof(IRelationalStoreReader.RecordSignals),
                nameof(IRelationalStoreReader.Sessions),
                nameof(IRelationalStoreReader.StoredFields),
            ]);

        MethodInfo? storedFields = typeof(IRelationalStoreReader).GetMethod(nameof(IRelationalStoreReader.StoredFields));
        Assert.NotNull(storedFields);
        Assert.Equal(typeof(StoredFieldsView), storedFields.ReturnType);
        Assert.Equal([typeof(string)], storedFields.GetParameters().Select(static p => p.ParameterType).ToArray());

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
    public void StoredFieldsView_ModelsCatalogTableEntriesColumnsRowsAndMissingParentKeys()
    {
        StoredTableFields table = new(
            TableName: "record_model",
            ColumnNames: ["span_id", "model_id"],
            Rows:
            [
                new StoredFieldRow(
                    [
                        new StoredFieldCell("span_id", SqliteColumnType.SqliteText, TextValue: "span-1"),
                        new StoredFieldCell("model_id", SqliteColumnType.SqliteText, TextValue: "claude-sonnet-4-5"),
                    ]),
            ],
            MissingParentKeys: ["span-2"]);

        StoredFieldsView view = new("session-1", [table]);

        Assert.Equal("session-1", view.SessionKey);
        Assert.Single(view.Tables);
        Assert.Equal("record_model", view.Tables[0].TableName);
        Assert.Equal(["span_id", "model_id"], view.Tables[0].ColumnNames);
        Assert.Single(view.Tables[0].Rows);
        Assert.Equal(["span-2"], view.Tables[0].MissingParentKeys);
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
