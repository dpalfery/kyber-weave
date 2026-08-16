using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Packaging;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>
/// Production implementation of <see cref="IApmRunner"/> executing the APM CLI tool
/// via <see cref="ProcessRunner"/> without shell execution.
/// </summary>
public sealed class ApmProcessRunner : IApmRunner
{
    private readonly IProcessExecutor _executor;
    private readonly string _executable;


    /// <summary>
    /// Initializes a new instance of <see cref="ApmProcessRunner"/> with the specified executor and executable.
    /// </summary>
    /// <param name="executor">The process executor boundary.</param>
    /// <param name="executable">The executable name or path on PATH (defaults to 'apm').</param>
    public ApmProcessRunner(IProcessExecutor executor, string executable = "apm")
    {
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
        _executable = string.IsNullOrWhiteSpace(executable) ? "apm" : executable;
    }

    /// <inheritdoc />
    public Task<ApmRenderResult> RenderAsync(ApmRenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ProcessStartInfo startInfo = CreateStartInfo(_executable, request.SourceDirectory);
        startInfo.ArgumentList.Add("compile");
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add(request.SourceDirectory);
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(string.Join(",", request.Targets.Select(SquadTargetCatalog.GetToken)));
        startInfo.ArgumentList.Add("--scope");
        startInfo.ArgumentList.Add(request.Scope == SquadDeploymentScope.Global ? "global" : "project");

        if (!string.IsNullOrWhiteSpace(request.UserScopeDirectory))
        {
            startInfo.ArgumentList.Add("--user-root");
            startInfo.ArgumentList.Add(request.UserScopeDirectory);
        }

        if (!string.IsNullOrWhiteSpace(request.TranslationMode))
        {
            startInfo.ArgumentList.Add("--translation");
            startInfo.ArgumentList.Add(request.TranslationMode);
        }

        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");

        ProcessResult processResult;
        try
        {
            processResult = _executor.Run(startInfo, string.Empty);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ApmRenderResult(
                Success: false,
                Files: [],
                Degradations: [],
                Warnings: [],
                Errors: [$"Failed to execute APM CLI ('{_executable}'): {ex.Message}"]));
        }

        if (processResult.ExitCode != 0)
        {
            List<string> errors = [];
            if (!string.IsNullOrWhiteSpace(processResult.StandardError))
            {
                errors.Add(processResult.StandardError.Trim());
            }
            else if (!string.IsNullOrWhiteSpace(processResult.StandardOutput))
            {
                try
                {
                    using JsonDocument errorDoc = JsonDocument.Parse(processResult.StandardOutput);
                    if (errorDoc.RootElement.TryGetProperty("errors", out JsonElement errorsElement) &&
                        errorsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement err in errorsElement.EnumerateArray())
                        {
                            string? msg = err.GetString();
                            if (!string.IsNullOrWhiteSpace(msg))
                            {
                                errors.Add(msg);
                            }
                        }
                    }
                }
                catch
                {
                    errors.Add(processResult.StandardOutput.Trim());
                }
            }

            if (errors.Count == 0)
            {
                errors.Add($"APM CLI compile failed with exit code {processResult.ExitCode}.");
            }

            return Task.FromResult(new ApmRenderResult(
                Success: false,
                Files: [],
                Degradations: [],
                Warnings: [],
                Errors: errors));
        }

        return Task.FromResult(ParseRenderOutput(processResult.StandardOutput, request.SourceDirectory));
    }

    /// <inheritdoc />
    public Task<ApmPackResult> PackAsync(ApmPackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string formatToken = request.Format switch
        {
            ApmPackFormat.Apm => "apm",
            ApmPackFormat.Plugins => "plugins",
            ApmPackFormat.All => "all",
            _ => "all"
        };

        ProcessStartInfo startInfo = CreateStartInfo(_executable, request.SourceDirectory);
        startInfo.ArgumentList.Add("pack");
        startInfo.ArgumentList.Add("--source");
        startInfo.ArgumentList.Add(request.SourceDirectory);
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add(formatToken);
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(request.OutputDirectory);
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add(request.Version);

        ProcessResult processResult;
        try
        {
            processResult = _executor.Run(startInfo, string.Empty);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ApmPackResult(
                Success: false,
                CreatedArchives: [],
                Errors: [$"Failed to execute APM pack ('{_executable}'): {ex.Message}"],
                Warnings: []));
        }

        if (processResult.ExitCode != 0)
        {
            string error = !string.IsNullOrWhiteSpace(processResult.StandardError)
                ? processResult.StandardError.Trim()
                : (!string.IsNullOrWhiteSpace(processResult.StandardOutput)
                    ? processResult.StandardOutput.Trim()
                    : $"APM CLI pack failed with exit code {processResult.ExitCode}.");

            return Task.FromResult(new ApmPackResult(
                Success: false,
                CreatedArchives: [],
                Errors: [error],
                Warnings: []));
        }

        return Task.FromResult(ParsePackOutput(request));
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string workingDirectory) =>
        new(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

    private static ApmRenderResult ParseRenderOutput(string standardOutput, string sourceDirectory)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return new ApmRenderResult(true, [], [], [], []);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(standardOutput);
            JsonElement root = doc.RootElement;

            bool success = true;
            if (root.TryGetProperty("success", out JsonElement successElement) &&
                successElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                success = successElement.GetBoolean();
            }

            List<SquadDeploymentFile> files = [];
            if (root.TryGetProperty("files", out JsonElement filesElement) &&
                filesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement fileObj in filesElement.EnumerateArray())
                {
                    string path = fileObj.TryGetProperty("path", out JsonElement pathElem)
                        ? pathElem.GetString() ?? string.Empty
                        : (fileObj.TryGetProperty("relativePath", out JsonElement relElem) ? relElem.GetString() ?? string.Empty : string.Empty);

                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    string target = fileObj.TryGetProperty("target", out JsonElement targetElem)
                        ? targetElem.GetString() ?? string.Empty
                        : string.Empty;

                    byte[] content;
                    if (fileObj.TryGetProperty("content", out JsonElement contentElement))
                    {
                        content = contentElement.ValueKind == JsonValueKind.String
                            ? System.Text.Encoding.UTF8.GetBytes(contentElement.GetString() ?? string.Empty)
                            : contentElement.GetBytesFromBase64();
                    }
                    else
                    {
                        string diskPath = Path.Combine(sourceDirectory, path.Replace('/', Path.DirectorySeparatorChar));
                        content = File.Exists(diskPath) ? File.ReadAllBytes(diskPath) : [];
                    }

                    string normalizedPath = SquadPathPolicy.NormalizeRelativePath(path);
                    files.Add(new SquadDeploymentFile(normalizedPath, content, target));
                }
            }

            List<ApmDegradationRecord> degradations = [];
            if (root.TryGetProperty("degradations", out JsonElement degradationsElement) &&
                degradationsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement deg in degradationsElement.EnumerateArray())
                {
                    string target = deg.GetProperty("target").GetString()!;
                    string canonicalId = deg.GetProperty("canonicalIdentity").GetString()!;
                    string outputId = deg.GetProperty("outputIdentity").GetString()!;
                    string code = deg.GetProperty("code").GetString()!;
                    string digest = deg.TryGetProperty("instructionDigest", out JsonElement digestElem)
                        ? digestElem.GetString() ?? string.Empty
                        : string.Empty;
                    string? details = deg.TryGetProperty("details", out JsonElement detailsElem)
                        ? detailsElem.GetString()
                        : null;

                    degradations.Add(new ApmDegradationRecord(
                        target,
                        canonicalId,
                        outputId,
                        code,
                        digest,
                        details));
                }
            }

            List<ApmWarning> warnings = [];
            if (root.TryGetProperty("warnings", out JsonElement warningsElement) &&
                warningsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement warn in warningsElement.EnumerateArray())
                {
                    string code = warn.GetProperty("code").GetString()!;
                    string message = warn.GetProperty("message").GetString()!;
                    string? target = warn.TryGetProperty("target", out JsonElement targetElem)
                        ? targetElem.GetString()
                        : null;
                    warnings.Add(new ApmWarning(code, message, target));
                }
            }

            List<string> errors = [];
            if (root.TryGetProperty("errors", out JsonElement errorsElement) &&
                errorsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement err in errorsElement.EnumerateArray())
                {
                    string? msg = err.GetString();
                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        errors.Add(msg);
                    }
                }
            }

            return new ApmRenderResult(
                Success: success && errors.Count == 0,
                Files: files,
                Degradations: degradations,
                Warnings: warnings,
                Errors: errors);
        }
        catch (JsonException ex)
        {
            return new ApmRenderResult(
                Success: false,
                Files: [],
                Degradations: [],
                Warnings: [],
                Errors: [$"Failed to parse APM render output JSON: {ex.Message}"]);
        }
    }

    private static ApmPackResult ParsePackOutput(ApmPackRequest request)
    {
        List<string> createdArchives = [];
        string? pluginManifestJson = null;

        if (request.Format is ApmPackFormat.Apm or ApmPackFormat.All)
        {
            string apmZip = Path.Combine(request.OutputDirectory, $"kyber-squad-{request.Version}.zip");
            if (File.Exists(apmZip))
            {
                createdArchives.Add(apmZip);
            }
        }

        if (request.Format is ApmPackFormat.Plugins or ApmPackFormat.All)
        {
            string pluginZip = Path.Combine(request.OutputDirectory, $"kyber-squad-plugin-{request.Version}.zip");
            if (File.Exists(pluginZip))
            {
                createdArchives.Add(pluginZip);

                try
                {
                    using ZipArchive zip = ZipFile.OpenRead(pluginZip);
                    ZipArchiveEntry? manifestEntry = zip.GetEntry(".agent-plugins/plugin.json")
                                                   ?? zip.GetEntry("plugin.json");
                    if (manifestEntry is not null)
                    {
                        using Stream stream = manifestEntry.Open();
                        using StreamReader reader = new(stream, System.Text.Encoding.UTF8);
                        pluginManifestJson = reader.ReadToEnd();
                    }
                }
                catch
                {
                    // Suppress zip reading errors during pack output parsing
                }
            }
        }

        return new ApmPackResult(
            Success: true,
            CreatedArchives: createdArchives,
            Errors: [],
            Warnings: [],
            PluginManifestJson: pluginManifestJson);
    }
}
