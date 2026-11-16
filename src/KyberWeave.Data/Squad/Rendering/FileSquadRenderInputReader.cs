using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>Reads a render's disk inputs from the Squad source tree and the user's Copilot config.</summary>
public sealed class FileSquadRenderInputReader : ISquadRenderInputReader
{
    /// <inheritdoc />
    public SquadRenderRequest Prepare(SquadRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DiskInputs is not null)
        {
            return request;
        }

        SquadSource? source = request.PreloadedSource;
        Exception? sourceFailure = null;
        if (source is null)
        {
            try
            {
                source = SquadSourceLoader.Load(request.SourceDirectory);
            }
            catch (Exception exception)
            {
                sourceFailure = exception;
            }
        }

        string? baselineBlock = null;
        Exception? baselineFailure = null;
        try
        {
            baselineBlock = SquadWorkerBaselineFile.ReadBlock(request.SourceDirectory);
        }
        catch (Exception exception)
        {
            baselineFailure = exception;
        }

        ISquadGlobalRootResolver? roots = request.GlobalRoots is null
            ? null
            : new MemoizedSquadGlobalRoots(request.GlobalRoots);
        Func<string, string?> environment = request.EnvironmentReader ?? Environment.GetEnvironmentVariable;
        (bool declared, string? overlayText, Exception? overlayFailure) =
            ReadOperatorChannel(request, environment, roots, source);

        return request with
        {
            EnvironmentReader = environment,
            PreloadedSource = source,
            GlobalRoots = roots,
            DiskInputs = new SquadRenderDiskInputs(
                sourceFailure,
                baselineBlock,
                baselineFailure,
                overlayText,
                overlayFailure,
                declared),
        };
    }

    private static (bool Declared, string? OverlayText, Exception? OverlayFailure) ReadOperatorChannel(
        SquadRenderRequest request,
        Func<string, string?> environment,
        ISquadGlobalRootResolver? roots,
        SquadSource? source)
    {
        if (request.Scope != SquadDeploymentScope.Global
            || roots is null
            || !request.Targets.Contains(SquadTarget.Copilot)
            || !SquadOperatorChannel.IsGateOn(environment))
        {
            return (false, null, null);
        }

        SquadOperatorChannelServerName server = SquadOperatorChannel.ResolveServerName(environment);
        if (server.Name is not { } serverName)
        {
            return (false, null, null);
        }

        string copilotRoot;
        try
        {
            copilotRoot = roots.ResolveGlobalRoot(SquadTarget.Copilot);
        }
        catch (ArgumentException)
        {
            // The render resolves the same root and reports the failure itself.
            return (false, null, null);
        }

        if (!CopilotMcpServerProbe.HasServer(copilotRoot, serverName))
        {
            return (false, null, null);
        }

        string overlaySourceDirectory = Directory.Exists(request.SourceDirectory)
            ? request.SourceDirectory
            : source?.RootPath ?? request.SourceDirectory;
        try
        {
            return (true, SquadOperatorChannelOverlayFile.ReadText(overlaySourceDirectory), null);
        }
        catch (SquadRenderValidationException exception)
        {
            return (true, null, exception);
        }
    }
}
