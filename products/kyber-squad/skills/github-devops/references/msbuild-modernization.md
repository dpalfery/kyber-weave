---
name: github-devops/msbuild-modernization
description: Modernize legacy MSBuild project files to SDK-style format, utilize modern property and item functions, and adopt Central Package Management.
---

# MSBuild Project Modernization

Use this reference when migrating legacy .NET Framework or verbose MSBuild project files (`.csproj`) to modern SDK-style projects, eliminating boilerplate items, and utilizing modern MSBuild functions.

---

## 1. Legacy vs SDK-Style Project Structure

### Legacy Project File (Verbose & Fragile)

Legacy projects require explicit file enumerations, GUIDs, assembly imports, and `ToolsVersion`:

```xml
<!-- LEGACY: Avoid this pattern -->
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" />
  <PropertyGroup>
    <ProjectGuid>{A1B2C3D4-E5F6-7890-ABCD-1234567890AB}</ProjectGuid>
    <OutputType>Library</OutputType>
    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="System" />
    <Reference Include="System.Core" />
    <Reference Include="Newtonsoft.Json, Version=13.0.0.0, Culture=neutral, PublicKeyToken=30ad4fe6b2a6aeed">
      <HintPath>..\packages\Newtonsoft.Json.13.0.1\lib\net45\Newtonsoft.Json.dll</HintPath>
    </Reference>
  </ItemGroup>
  <ItemGroup>
    <Compile Include="Models\User.cs" />
    <Compile Include="Services\UserService.cs" />
    <Compile Include="Properties\AssemblyInfo.cs" />
  </ItemGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
```

### Modern SDK-Style Project File (Minimal & Declarative)

SDK-style projects use default globbing (auto-including all `**/*.cs` files), automatic framework references, and clean dependencies:

```xml
<!-- MODERN SDK-STYLE -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
</Project>
```

---

## 2. Step-by-Step Modernization Recipe

### Step 1: Replace Project Header and Remove Imports

Replace the root `<Project>` element with `<Project Sdk="Microsoft.NET.Sdk">` (or `Microsoft.NET.Sdk.Web` for web apps). Remove:
- `ToolsVersion="..."`
- `xmlns="http://schemas.microsoft.com/developer/msbuild/2003"`
- `<ProjectGuid>` and `<ProjectTypeGuids>`
- Explicit `<Import>` elements for `Microsoft.Common.props` and `Microsoft.CSharp.targets`

### Step 2: Remove Default Item Globs

Delete explicit `<Compile Include="..." />`, `<None Include="..." />`, and `<EmbeddedResource Include="..." />` items that match files under the project folder. The SDK includes these automatically.

*Only retain items with non-default metadata (e.g. `<None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />`).*

### Step 3: Migrate `packages.config` to `PackageReference`

Remove `packages.config` files and convert all entries to `<PackageReference>` elements:

```xml
<!-- Replace HintPath references with PackageReference -->
<ItemGroup>
  <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  <PackageReference Include="Dapper" Version="2.1.35" />
</ItemGroup>
```

### Step 4: Delete `AssemblyInfo.cs` Duplications

Modern SDKs automatically generate assembly attributes (`AssemblyTitle`, `AssemblyVersion`, `AssemblyCompany`) from project properties.
- Either delete manual attributes in `Properties/AssemblyInfo.cs`, or
- Add `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>` during phased migration.

---

## 3. Modern MSBuild Property Functions

Modern MSBuild supports .NET string and path methods directly in expressions:

### String Operations

```xml
<PropertyGroup>
  <!-- Replace characters or substrings -->
  <CleanVersion>$(PackageVersion.Replace('-preview', ''))</CleanVersion>

  <!-- Substring extraction -->
  <MajorVersion>$(PackageVersion.Substring(0, $(PackageVersion.IndexOf('.'))))</MajorVersion>

  <!-- Formatting -->
  <ArtifactName>$([System.String]::Format('{0}-{1}', $(MSBuildProjectName), $(Configuration)))</ArtifactName>
</PropertyGroup>
```

### Path & Directory Functions

```xml
<PropertyGroup>
  <!-- Combine directory paths -->
  <ArtifactsDir>$([System.IO.Path]::Combine('$(MSBuildThisFileDirectory)', '..', 'artifacts'))</ArtifactsDir>

  <!-- Normalize to absolute path with forward slashes -->
  <NormalizedArtifactsDir>$([MSBuild]::NormalizeDirectory('$(ArtifactsDir)'))</NormalizedArtifactsDir>

  <!-- Find file in parent directory tree -->
  <RootPropsPath>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))</RootPropsPath>
</PropertyGroup>
```

### Operating System and Condition Functions

```xml
<PropertyGroup>
  <IsWindowsRunner>$([MSBuild]::IsOSPlatform('Windows'))</IsWindowsRunner>
  <IsUnixRunner>$([MSBuild]::IsOSPlatform('Linux'))</IsUnixRunner>
  <IsMacRunner>$([MSBuild]::IsOSPlatform('OSX'))</IsMacRunner>
</PropertyGroup>
```

---

## 4. Modern Item Functions and Transforms

Manipulate item collections concisely without writing custom tasks:

```xml
<ItemGroup>
  <!-- Distinct items based on metadata or identity -->
  <UniqueAssemblies Include="@(ReferenceCopyLocalPaths->Distinct())" />

  <!-- Count items in collection -->
  <TotalSourceFilesCount>@(Compile->Count())</TotalSourceFilesCount>

  <!-- Filter items with specific metadata -->
  <ServiceContracts Include="@(Compile)" Condition="'%(Compile.IsContract)' == 'true'" />

  <!-- Item transform mapping paths -->
  <GeneratedOutputs Include="@(SchemaFile->'$(IntermediateOutputPath)%(Filename).cs')" />
</ItemGroup>
```

---

## 5. Multi-Targeting Frameworks

For shared libraries supporting multiple runtime versions:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Multi-target .NET 8 and .NET 9 -->
    <TargetFrameworks>net8.0;net9.0</TargetFrameworks>
  </PropertyGroup>

  <!-- Target-specific dependencies -->
  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
    <PackageReference Include="Microsoft.Bcl.AsyncInterfaces" Version="8.0.0" />
  </ItemGroup>
</Project>
```

---

## 6. Modernization Checklist

- [ ] Project uses `<Project Sdk="Microsoft.NET.Sdk">`.
- [ ] No explicit `<Compile Include="..." />` items for files present in project subdirectories.
- [ ] All `packages.config` files removed and converted to `<PackageReference>` or Central Package Management.
- [ ] No hardcoded `<Reference Include="..." HintPath="...">` pointing into local file paths or packages directories.
- [ ] Redundant `AssemblyInfo.cs` attributes removed or replaced by project properties.
- [ ] Uses MSBuild property/item functions instead of custom `<Exec>` scripts for path manipulation.
