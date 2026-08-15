using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using KyberWeave.Core.Agents.Model;

namespace KyberWeave.Core.Agents.Parsing;

/// <summary>
/// Parser for TOML-based agent definition files (e.g. Codex .toml files).
/// </summary>
public sealed partial class TomlAgentParser : IAgentParser
{
    public bool CanParse(string filePath) => filePath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase);

    public AgentModel Parse(string filePath, HarnessKind harness)
    {
        string content = File.ReadAllText(filePath);
        string dirName = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        string fileName = Path.GetFileNameWithoutExtension(filePath);

        string name = fileName;
        string description = string.Empty;
        string instructions = string.Empty;
        string model = string.Empty;
        Collection<string> tools = new Collection<string>();
        Dictionary<string, string> metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Simple TOML line-and-block extractor
        string[] lines = content.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;

            // Multiline string key check (developer_instructions, instructions, system_prompt)
            Match multilineMatch = MyRegex().Match(line);
            if (multilineMatch.Success)
            {
                string key = multilineMatch.Groups["key"].Value;
                string quoteSymbol = line.Substring(line.IndexOf('=') + 1).Trim();
                List<string> blockLines = new List<string>();
                i++;
                while (i < lines.Length)
                {
                    if (lines[i].Trim() == quoteSymbol) break;
                    blockLines.Add(lines[i]);
                    i++;
                }
                string body = string.Join("\n", blockLines).Trim();
                if (key.Equals("developer_instructions", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("instructions", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("system_prompt", StringComparison.OrdinalIgnoreCase))
                {
                    instructions = body;
                }
                else
                {
                    metadata[key] = body;
                }
                continue;
            }

            // Single line key-value: key = "value"
            Match kvMatch = Regex.Match(line, @"^(?<key>[A-Za-z0-9_\-]+)\s*=\s*(?:\u0022(?<val>.*?)\u0022|'(?<val>.*?)'|(?<val>[^\u0022'].*))$");
            if (kvMatch.Success)
            {
                string key = kvMatch.Groups["key"].Value;
                string val = kvMatch.Groups["val"].Value.Trim();

                if (key.Equals("name", StringComparison.OrdinalIgnoreCase)) name = val;
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) description = val;
                else if (key.Equals("model", StringComparison.OrdinalIgnoreCase)) model = val;
                else if (key.Equals("developer_instructions", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("instructions", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("system_prompt", StringComparison.OrdinalIgnoreCase))
                {
                    instructions = val;
                }
                else
                {
                    metadata[key] = val;
                }
            }
        }

        return new AgentModel
        {
            RoleName = name,
            Harness = harness,
            FilePath = Path.GetFullPath(filePath),
            DirectoryPath = dirName,
            Description = description,
            InstructionsBody = instructions,
            ModelPreference = model,
            Tools = tools,
            FrontmatterOrMetadata = metadata
        };
    }

    [GeneratedRegex(@"^(?<key>[A-Za-z0-9_\-]+)\s*=\s*(?:''''''|""""""|'''|"""""")$")]
    private static partial Regex MyRegex();
}
