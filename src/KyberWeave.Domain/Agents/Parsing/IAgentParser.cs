using KyberWeave.Core.Agents.Model;

namespace KyberWeave.Core.Agents.Parsing;

/// <summary>
/// Interface for parsing harness-specific agent definition files into an <see cref="AgentModel"/>.
/// </summary>
public interface IAgentParser
{
    /// <summary>Gets whether this parser handles the file at <paramref name="filePath"/>.</summary>
    /// <param name="filePath">The agent definition's path.</param>
    /// <returns>True when this parser can parse it.</returns>
    bool CanParse(string filePath);

    /// <summary>Parses an agent definition.</summary>
    /// <param name="content">The file's text, read by the caller.</param>
    /// <param name="filePath">The file's path, used for the model's identity and directory.</param>
    /// <param name="harness">The harness the file belongs to.</param>
    /// <returns>The parsed agent.</returns>
    AgentModel Parse(string content, string filePath, HarnessKind harness);
}
