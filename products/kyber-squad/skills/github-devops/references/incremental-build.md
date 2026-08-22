---
name: github-devops/incremental-build
description: Author and troubleshoot MSBuild incremental builds, up-to-date checks, Inputs/Outputs target declarations, and target batching.
---

# Incremental Builds & Up-to-Date Checks

Use this reference when targets execute repeatedly despite no input changes, incremental builds fail to skip targets, or custom target `Inputs` and `Outputs` require configuration.

---

## 1. How MSBuild Incremental Build Works

MSBuild determines whether to run a target by comparing the timestamps of its declared `Inputs` against its `Outputs`:

- **Target runs:** If any input file is newer than any output file, or if any declared output file does not exist.
- **Target skips (up-to-date):** If all output files exist and are newer than or equal to all input files.
- **Target always runs:** If `Inputs` or `Outputs` attributes are missing from the `<Target>` element.

```xml
<Target Name="GenerateClientCode"
        Inputs="@(OpenApiSpecification)"
        Outputs="$(IntermediateOutputPath)GeneratedClient.cs">
  <Exec Command="nswag run /input:%(OpenApiSpecification.Identity) /out:$(IntermediateOutputPath)GeneratedClient.cs" />
</Target>
```

---

## 2. Writing Incremental Targets Correctly

### 1-to-1 File Transformations

When each input maps to a corresponding output file:

```xml
<ItemGroup>
  <Protobuf Include="Protos/**/*.proto" />
</ItemGroup>

<Target Name="CompileProtobuf"
        Inputs="@(Protobuf)"
        Outputs="@(Protobuf->'$(IntermediateOutputPath)%(Filename).cs')">
  <Exec Command="protoc --csharp_out=$(IntermediateOutputPath) %(Protobuf.Identity)" />
</Target>
```

### Many-to-1 Aggregation Transformations

When multiple input files produce a single combined output file (e.g. bundled assets or single generated source):

```xml
<ItemGroup>
  <ConfigTemplate Include="Templates/**/*.json" />
</ItemGroup>

<Target Name="BundleTemplates"
        Inputs="@(ConfigTemplate)"
        Outputs="$(IntermediateOutputPath)bundled-templates.json">
  <BundleTemplatesTask Sources="@(ConfigTemplate)"
                       Destination="$(IntermediateOutputPath)bundled-templates.json" />
</Target>
```

### 1-to-Many Transformations via Marker / Stamp Files

When a tool produces unpredictable or dynamic output files, use a `.stamp` / `.marker` file to record completion time:

```xml
<Target Name="GenerateComplexAssets"
        Inputs="@(AssetSource)"
        Outputs="$(IntermediateOutputPath)assets.stamp">
  <Exec Command="asset-generator --out $(IntermediateOutputPath)assets/" />
  <!-- Touch marker file upon successful execution -->
  <Touch Files="$(IntermediateOutputPath)assets.stamp" AlwaysCreate="true" />
</Target>
```

---

## 3. Target Batching vs Item Transform Batching

### Target Batching

When `Outputs` contains a metadata reference like `%(MetadataName)`, MSBuild runs the target once per distinct metadata bucket:

```xml
<!-- Runs once per distinct TargetFramework or Culture -->
<Target Name="ProcessLocalizedResources"
        Inputs="@(ResourceFile)"
        Outputs="@(ResourceFile->'$(OutputPath)%(Culture)/%(Filename).resources')">
  <GenerateResource Sources="@(ResourceFile)" />
</Target>
```

### Task / Item Batching (Inside a Single Target Execution)

When metadata is passed to a task parameter instead of the target's `Outputs`:

```xml
<Target Name="CopyAssets" DependsOnTargets="PrepareAssets">
  <!-- The Copy task batches internally for each distinct DestinationSubDirectory -->
  <Copy SourceFiles="@(Asset)"
        DestinationFolder="$(OutputPath)assets/%(Asset.DestinationSubDirectory)" />
</Target>
```

---

## 4. Diagnosing Broken Incremental Builds

If MSBuild rebuilds projects or executes targets when nothing changed:

### Diagnostic Logging

Run a diagnostic build to inspect why targets executed:

```bash
dotnet build -v:diagnostic -fl -flp:logfile=diag.log
```

Search `diag.log` for the target name:
- `"Building target '<TargetName>' completely."` — Indicates missing outputs or newer inputs.
- `"Target '<TargetName>' is up-to-date with respect to input files."` — Indicates successfully skipped target.
- `"Input file '<Path>' is newer than output file '<Path>'."` — Pinpoints the exact file causing the rebuild.

### Common Causes of Broken Incremental Builds

| Cause | Symptom | Fix |
|---|---|---|
| Missing `Inputs` / `Outputs` | Target executes on every single build | Add explicit `Inputs` and `Outputs` attributes |
| Output file never written | Target executes on every build | Ensure the task actually writes all files listed in `Outputs` (or use a `.stamp` marker) |
| Output timestamp older than input | Tool preserves original timestamp | Use `<Touch Files="@(Output)" />` after the task |
| Dynamic timestamps generated in source | Generated code contains `DateTime.UtcNow` | Strip timestamp comments or make generation deterministic |
| Target modifies an upstream input | Upstream project continually rebuilds | Do not write to source directories; write to `$(IntermediateOutputPath)` |

---

## 5. Visual Studio FastUpToDateCheck

The Visual Studio and .NET project system uses the `FastUpToDateCheck` to determine if a project can bypass MSBuild entirely during F5 / debugging sessions:

```xml
<ItemGroup>
  <!-- Tell the fast up-to-date check about custom inputs/outputs -->
  <UpToDateCheckInput Include="@(CustomSchemaFiles)" />
  <UpToDateCheckOutput Include="$(IntermediateOutputPath)schema.generated.cs" />
</ItemGroup>
```

To disable the fast check when diagnosing up-to-date discrepancies:

```xml
<PropertyGroup>
  <DisableFastUpToDateCheck>true</DisableFastUpToDateCheck>
</PropertyGroup>
```

---

## 6. Incremental Build Checklist

- [ ] All custom `<Target>` definitions declare both `Inputs` and `Outputs`.
- [ ] Outputs are written to `$(IntermediateOutputPath)` (`obj/`) or `$(OutputPath)` (`bin/`), not source folders.
- [ ] Tools that produce variable outputs use a `<Touch>` marker / stamp file.
- [ ] No task writes to a file declared in another project's compile or resource inputs.
- [ ] Diagnostic logs confirm targets report `is up-to-date` when run consecutively without changes.
