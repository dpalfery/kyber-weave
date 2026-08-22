---
name: github-devops/msbuild-anti-patterns
description: Avoid and resolve common MSBuild anti-patterns — hardcoded OS paths, design-time build interference, unquoted conditions, shell execution, and circular references.
---

# MSBuild Anti-Patterns & Common Pitfalls

Use this reference when diagnosing cross-platform build failures, IDE sluggishness or locks during design-time evaluation, circular target dependencies, or fragile custom MSBuild scripts.

---

## 1. Hardcoded OS Path Separators

### Anti-Pattern

Using Windows-style backslashes (`\`) or hardcoded absolute drive letters (`C:\...`):

```xml
<!-- ANTI-PATTERN: Fails on Linux and macOS CI runners -->
<PropertyGroup>
  <DocGenOutput>$(MSBuildProjectDirectory)\bin\docs\output.xml</DocGenOutput>
</PropertyGroup>
<Import Project="..\build\Common.props" />
```

### Best Practice

Always use standard forward slashes (`/`) in MSBuild paths. MSBuild normalizes forward slashes across Windows, Linux, and macOS automatically:

```xml
<!-- CORRECT: Works across all operating systems -->
<PropertyGroup>
  <DocGenOutput>$(MSBuildProjectDirectory)/bin/docs/output.xml</DocGenOutput>
</PropertyGroup>
<Import Project="../build/Common.props" />
```

For dynamic path combinations, use `$([MSBuild]::NormalizePath(...))` or `$([System.IO.Path]::Combine(...))`.

---

## 2. Executing Heavy Tasks During Design-Time Builds

### Anti-Pattern

Running custom `<Exec>`, code generation tools, or network calls during IDE evaluation without guarding against design-time builds:

```xml
<!-- ANTI-PATTERN: Runs during Visual Studio / VS Code IntelliSense background evaluation -->
<Target Name="GenerateApiClient" BeforeTargets="CoreCompile">
  <Exec Command="nswag run openapi.json" />
</Target>
```

When design-time builds execute heavy tasks, IDEs freeze, code completion lags, and file locking errors occur.

### Best Practice

Guard custom targets with `Condition="'$(DesignTimeBuild)' != 'true'"` and `Condition="'$(BuildingProject)' == 'true'"`:

```xml
<!-- CORRECT: Skips during IDE design-time IntelliSense indexing -->
<Target Name="GenerateApiClient"
        BeforeTargets="CoreCompile"
        Condition="'$(DesignTimeBuild)' != 'true' and '$(BuildingProject)' == 'true'">
  <Exec Command="nswag run openapi.json" />
</Target>
```

---

## 3. Invoking Shell Commands via `<Exec>` for Native Operations

### Anti-Pattern

Invoking platform-specific shell tools (`cmd.exe`, `bash`, `mkdir`, `cp`, `rm`, `powershell`) via `<Exec>`:

```xml
<!-- ANTI-PATTERN: Fails depending on whether runner is Windows or Linux -->
<Target Name="CleanExtraFiles" AfterTargets="Clean">
  <Exec Command="rm -rf $(TargetDir)temp_data" /> <!-- Crashes on Windows cmd -->
</Target>
```

### Best Practice

Use MSBuild's built-in, cross-platform tasks:

```xml
<!-- CORRECT: Native cross-platform tasks -->
<Target Name="CleanExtraFiles" AfterTargets="Clean">
  <RemoveDir Directories="$(TargetDir)temp_data" />
  <MakeDir Directories="$(TargetDir)temp_data" />
  <Delete Files="@(StaleFiles)" />
  <Copy SourceFiles="@(SourceFiles)" DestinationFolder="$(TargetDir)" />
  <WriteLinesToFile File="$(IntermediateOutputPath)build.meta" Lines="$(BuildNumber)" Overwrite="true" />
</Target>
```

---

## 4. Unquoted Property Conditions

### Anti-Pattern

Evaluating properties without surrounding single quotes in `Condition`:

```xml
<!-- ANTI-PATTERN: Crashes with MSB4113 if $(CustomFeatureEnabled) is blank/empty -->
<PropertyGroup Condition="$(CustomFeatureEnabled) == true">
  <DefineConstants>$(DefineConstants);ENABLE_CUSTOM</DefineConstants>
</PropertyGroup>
```

When `$(CustomFeatureEnabled)` is undefined or empty, MSBuild parses the condition as ` == true`, causing an evaluation syntax error.

### Best Practice

Always wrap property expansions in single quotes:

```xml
<!-- CORRECT: Safely handles empty or undefined properties -->
<PropertyGroup Condition="'$(CustomFeatureEnabled)' == 'true'">
  <DefineConstants>$(DefineConstants);ENABLE_CUSTOM</DefineConstants>
</PropertyGroup>
```

---

## 5. Circular Project and Target Dependencies

### Anti-Pattern

- **Project level:** `Project A` references `Project B`, which references `Project A` (directly or transitively).
- **Target level:** `Target A` specifies `DependsOnTargets="Target B"`, while `Target B` specifies `DependsOnTargets="Target A"`.

### Best Practice

- Extract shared contracts, interfaces, and DTOs to an independent `Contracts` library.
- Adhere to Clean Architecture layering: domain models and interfaces never reference application, infrastructure, or UI layers.
- For MSBuild targets, define a clean DAG (Directed Acyclic Graph) using standard lifecycle hooks (`BeforeTargets="BeforeBuild"`, `AfterTargets="Build"`).

---

## 6. Modifying Source Files Inside Build Targets

### Anti-Pattern

Writing generated files or mutating code inside the source tree (`src/`) during build:

```xml
<!-- ANTI-PATTERN: Modifies tracked git sources, breaking incremental builds and dirtying working trees -->
<Target Name="StampVersion" BeforeTargets="CoreCompile">
  <WriteLinesToFile File="$(MSBuildProjectDirectory)/Version.cs" Lines="public static class V { ... }" />
</Target>
```

Writing to source directories causes:
- Git working tree becomes dirty during CI runs.
- MSBuild's timestamp comparison detects modified inputs, triggering endless re-compilation.

### Best Practice

Always output generated code to `$(IntermediateOutputPath)` (`obj/`) and include it dynamically in `@(Compile)`:

```xml
<!-- CORRECT: Writes to obj/ folder and includes in compilation dynamically -->
<Target Name="StampVersion" BeforeTargets="CoreCompile">
  <PropertyGroup>
    <GeneratedVersionFile>$(IntermediateOutputPath)Version.g.cs</GeneratedVersionFile>
  </PropertyGroup>
  <WriteLinesToFile File="$(GeneratedVersionFile)" Lines="public static class V { ... }" Overwrite="true" />
  <ItemGroup>
    <Compile Include="$(GeneratedVersionFile)" />
    <FileWrites Include="$(GeneratedVersionFile)" />
  </ItemGroup>
</Target>
```

---

## 7. Legacy `<Reference>` with Hardcoded `HintPath`

### Anti-Pattern

Referencing DLLs through relative paths into local folder structures:

```xml
<!-- ANTI-PATTERN: Fragile relative path breaks when directory structures shift -->
<ItemGroup>
  <Reference Include="MySharedLib">
    <HintPath>..\..\packages\MySharedLib.1.0.0\lib\net8.0\MySharedLib.dll</HintPath>
  </Reference>
</ItemGroup>
```

### Best Practice

Use modern NuGet package references (`<PackageReference>`) or project references (`<ProjectReference>`):

```xml
<!-- CORRECT -->
<ItemGroup>
  <PackageReference Include="MySharedLib" />
  <!-- or for local multi-project solutions -->
  <ProjectReference Include="../MySharedLib/MySharedLib.csproj" />
</ItemGroup>
```

---

## 8. Anti-Patterns Audit Checklist

- [ ] All file and directory paths use forward slashes (`/`).
- [ ] Custom targets check `Condition="'$(DesignTimeBuild)' != 'true'"`.
- [ ] No `<Exec>` calls for basic file operations (`rm`, `mkdir`, `cp`, `del`).
- [ ] All property conditions are wrapped in single quotes: `Condition="'$(Var)' == 'value'"`.
- [ ] Build targets write only to `$(IntermediateOutputPath)` or `$(OutputPath)`, never source folders.
- [ ] No circular project or target dependencies exist.
- [ ] No `<Reference>` items with relative `HintPath` to packages folders.
