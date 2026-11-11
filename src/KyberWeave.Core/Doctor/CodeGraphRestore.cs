namespace KyberWeave.Core.Doctor;

/// <summary>Which tracked files a CodeGraph index run restores after it writes them.</summary>
public enum CodeGraphRestore
{
    /// <summary>Restore every tracked file the index run wrote, the default for doctor and run-start callers.</summary>
    AllWrittenFiles,

    /// <summary>Restore only the tracked files <c>codegraph init</c> writes, leaving any other edit in place.</summary>
    InitConfigFilesOnly,
}
