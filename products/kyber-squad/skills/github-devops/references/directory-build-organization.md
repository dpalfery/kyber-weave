---
name: github-devops/directory-build-organization
description: Centralize and structure repository-wide MSBuild configuration using Directory.Build.props, Directory.Build.targets, and Directory.Packages.props.
---

# Directory.Build Organization & Multi-Project Coordination

Use this reference when organizing repository-wide build settings, establishing multi-level `.props`/`.targets` hierarchies, or centralizing package dependencies across multiple projects.

---

## 1. MSBuild File Evaluation Order

The SDK imports `Directory.Build.props` and `Directory.Build.targets` automatically based on directory hierarchy:

```
1. Directory.Build.props (Root)
2. Directory.Build.props (Sub-folder, if chained)
3. Microsoft.NET.Sdk.props (SDK default properties)
4. *.csproj (Project-specific properties and items)
5. Microsoft.NET.Sdk.targets (SDK default targets)
6. Directory.Build.targets (Sub-folder, if chained)
7. Directory.Build.targets (Root)
```

### Core Rule: Props vs Targets

- **`Directory.Build.props`:** Define default properties (`<PropertyGroup>`) that projects can override. Evaluated *before* project contents.
- **`Directory.Build.targets`:** Define property overrides, item definitions, and `<Target>` declarations that depend on properties set inside `.csproj` files. Evaluated *after* project contents.

---

## 2. Root `Directory.Build.props` Configuration

Place a single `Directory.Build.props` at the repository root to declare baseline settings for all projects:

```xml
<Project>
  <PropertyGroup>
    <!-- Language & Runtime Baseline -->
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <!-- Code Quality & Governance -->
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <AnalysisMode>All</AnalysisMode>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>

    <!-- Deterministic Builds & Source Link -->
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(GITHUB_ACTIONS)' == 'true'">true</ContinuousIntegrationBuild>
  </PropertyGroup>
</Project>
```

---

## 3. Multi-Level Hierarchy and Chaining

By default, MSBuild searches upward from the project directory and loads only the **first** `Directory.Build.props` or `Directory.Build.targets` it finds.

To specialize subtrees (e.g. `src/` vs `tests/`) while retaining root configuration, chain the parent file explicitly:

### `tests/Directory.Build.props` (Chaining Pattern)

```xml
<Project>
  <!-- 1. Import parent Directory.Build.props from repository root -->
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))"
          Condition="'$([MSBuild]::GetPathOfFileAbove(\'Directory.Build.props\', \'$(MSBuildThisFileDirectory)../\'))' != ''" />

  <!-- 2. Test-specific property overrides -->
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <!-- Allow test suites more permissive warning rules if needed -->
    <NoWarn>$(NoWarn);CA1707;CS1591</NoWarn>
  </PropertyGroup>

  <!-- 3. Test-specific shared package dependencies -->
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
```

### `src/Directory.Build.props` (Production Library Pattern)

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))"
          Condition="'$([MSBuild]::GetPathOfFileAbove(\'Directory.Build.props\', \'$(MSBuildThisFileDirectory)../\'))' != ''" />

  <PropertyGroup>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <IsPackable>true</IsPackable>
  </PropertyGroup>
</Project>
```

---

## 4. Central Package Management (CPM) with `Directory.Packages.props`

Central Package Management declares NuGet package versions in a single repository-level file, ensuring all projects reference identical versions without duplication.

### Root `Directory.Packages.props`

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>

  <ItemGroup>
    <!-- Core dependencies -->
    <PackageVersion Include="Markdig" Version="0.37.0" />
    <PackageVersion Include="YamlDotNet" Version="16.1.3" />
    <PackageVersion Include="Spectre.Console" Version="0.49.1" />

    <!-- Test dependencies -->
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageVersion Include="xunit.v3" Version="0.5.0-pre.32" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.0.0-pre.49" />
    <PackageVersion Include="coverlet.collector" Version="6.0.2" />
  </ItemGroup>

  <!-- Global Package References: Included in every project automatically -->
  <ItemGroup>
    <GlobalPackageReference Include="SonarAnalyzer.CSharp" Version="9.32.0.97167">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </GlobalPackageReference>
  </ItemGroup>
</Project>
```

### Consuming Packages in Project Files (`.csproj`)

In individual project files, omit the `Version` attribute:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <!-- Version is resolved from Directory.Packages.props -->
    <PackageReference Include="Markdig" />
    <PackageReference Include="YamlDotNet" />
  </ItemGroup>
</Project>
```

### Overriding Versions in Exceptional Projects

If a specific project requires an isolated version:

```xml
<PackageReference Include="YamlDotNet" VersionOverride="15.3.0" />
```

---

## 5. Root `Directory.Build.targets` Patterns

Use `Directory.Build.targets` for rules that inspect final project properties:

```xml
<Project>
  <!-- Enforce documentation comments only when GenerateDocumentationFile is true -->
  <PropertyGroup Condition="'$(GenerateDocumentationFile)' == 'true'">
    <NoWarn>$(NoWarn);CS1591</NoWarn> <!-- Suppress or enforce missing XML comments -->
  </PropertyGroup>

  <!-- Common post-build or artifact collection hooks -->
  <Target Name="LogBuildArtifact" AfterTargets="Build" Condition="'$(ContinuousIntegrationBuild)' == 'true'">
    <Message Importance="High" Text="Built project $(MSBuildProjectName) -> $(TargetPath)" />
  </Target>
</Project>
```

---

## 6. Directory.Build Organization Checklist

- [ ] Single root `Directory.Build.props` defines baseline properties and code analysis rules.
- [ ] Single root `Directory.Packages.props` manages all package versions centrally (`<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`).
- [ ] Sub-level `Directory.Build.props` files explicitly chain parent using `$([MSBuild]::GetPathOfFileAbove(...))`.
- [ ] Project files (`.csproj`) do not repeat versions, SDK baselines, or nullable/implicit using settings.
- [ ] Targets requiring final property evaluation reside in `Directory.Build.targets`, not `.props`.
