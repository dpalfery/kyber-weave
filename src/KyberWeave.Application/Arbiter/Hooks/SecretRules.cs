using System.Text;
using System.Text.RegularExpressions;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The worker guard's secret rule, <c>KW-ARB-SECRET-001</c>: the pure decision about one subagent's tool call, made
/// from the call's own text. It refuses a call that would read a secret file, that would print a secret from a store,
/// or that would carry a literal secret on the command line, because everything a tool call prints enters the
/// transcript, and the transcript goes to the model provider.
/// </summary>
/// <remarks>
/// The whole command text is matched, quotes and nested shell strings included, so wrapping a command in
/// <c>bash -c</c>, <c>sh -c</c> or <c>pwsh -Command</c> escapes nothing. A command whose text cannot be read is
/// refused rather than allowed, which is why <see cref="Refusal" /> catches its own failure. Listing a directory or
/// testing whether a file exists is not a read, and a value passed by reference (<c>"$VAR"</c>) never enters the
/// transcript, so both are allowed.
/// </remarks>
internal static partial class SecretRules
{
    /// <summary>The rule id a call that would expose a secret is refused under.</summary>
    internal const string SecretRuleId = "KW-ARB-SECRET-001";

    /// <summary>The words a command reads a file's contents with, so a secret path after one is a read.</summary>
    private static readonly HashSet<string> ReadingPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "cat", "head", "tail", "less", "more", "type", "get-content", "gc", "bat", "xxd", "strings", "base64",
        "cp", "scp", "grep", "rg", "egrep", "fgrep", "awk", "sed", "sort", "uniq", "od", "hexdump", "nl",
        "jq", "yq", "openssl", "diff", "cut", "tr", "dd", "select-string",
    };

    /// <summary>
    /// The readers whose first argument is a pattern, script or filter rather than a file, so only the arguments
    /// after it can name a file they read.
    /// </summary>
    private static readonly HashSet<string> PatternFirstPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "grep", "rg", "egrep", "fgrep", "awk", "sed", "jq", "yq",
    };

    /// <summary>The name fragments that make a file a secret wherever it sits.</summary>
    private static readonly string[] SecretNameFragments =
        ["password", "passwd", "secret", "credential", "token", "apikey", "api-key", "api_key"];

    /// <summary>
    /// The extensions whose files may be named like a secret, because they carry configuration or data. A file with
    /// no extension is also covered; a source or documentation file is not.
    /// </summary>
    private static readonly string[] ConfigurationAndDataExtensions =
    [
        ".txt", ".json", ".yaml", ".yml", ".xml", ".ini", ".conf", ".cfg", ".config", ".properties", ".toml",
        ".env", ".secret", ".pass",
    ];

    /// <summary>The extensions that carry a key or a certificate.</summary>
    private static readonly string[] SecretExtensions = [".pem", ".key", ".pfx", ".p12"];

    /// <summary>The whole file names that are a store of credentials wherever they sit.</summary>
    private static readonly HashSet<string> SecretFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".netrc", ".git-credentials", ".npmrc", ".pypirc",
    };

    /// <summary>The dotenv files that are examples, not a live configuration.</summary>
    private static readonly HashSet<string> ExampleDotEnvFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env.example", ".env.sample", ".env.template",
    };

    /// <summary>The words that may stand before a command without being one, including a shell's own command option.</summary>
    private static readonly HashSet<string> CommandWrappers = new(StringComparer.Ordinal)
    {
        "sudo", "command", "exec", "nohup", "time", "-c", "-command", "/c",
    };

    /// <summary>The refusal this call earns, or null when it would put no secret into the transcript.</summary>
    internal static string? Refusal(WorkerGuardRules.ToolCall call)
    {
        try
        {
            return Decide(call);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A command whose text the rule cannot read is not allowed through: that is the fail-closed reading.
            return RefusalText(SecretCase.UnreadableCommand);
        }
    }

    /// <summary>The one refusal text, naming which of the three cases fired in its first clause.</summary>
    internal static string RefusalText(SecretCase secretCase) =>
        $"{SecretRuleId}: this command would put a secret into the transcript, which is sent to the model, " +
        $"{CaseClause(secretCase)}. Never read or print a password, key or token. Let the tool read its own " +
        "configuration (user-secrets, a container's environment), or pass the value by reference (for example " +
        "sh -c '... \"$VAR\"') so it never appears in the command or its output. If the task truly needs a secret, " +
        "stop and return BLOCKED with an Open item: line saying so.";

    private static string CaseClause(SecretCase secretCase) => secretCase switch
    {
        SecretCase.ReadsSecretFile => "because it reads a secret file",
        SecretCase.PrintsStoredSecret => "because it prints a secret from a store",
        SecretCase.PutsLiteralSecret => "because it puts a literal secret on the command line",
        _ => "because it could not be read safely",
    };

    private static string? Decide(WorkerGuardRules.ToolCall call)
    {
        if (call.ReadPath is { Length: > 0 } readPath && IsSecretFile(readPath))
        {
            return RefusalText(SecretCase.ReadsSecretFile);
        }

        if (call.Command is not { Length: > 0 } command)
        {
            return null;
        }

        foreach (string step in Steps(command))
        {
            if (ReadsSecretFile(step))
            {
                return RefusalText(SecretCase.ReadsSecretFile);
            }

            if (PrintsStoredSecret(step))
            {
                return RefusalText(SecretCase.PrintsStoredSecret);
            }

            if (PutsLiteralSecret(step))
            {
                return RefusalText(SecretCase.PutsLiteralSecret);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether this step reads a secret file: a reading program takes one as an argument, a single <c>&lt;</c>
    /// redirect feeds one to the step, or a script's <c>open(...)</c> call names one. Listing a directory and
    /// testing a file names no reader, so neither is judged here.
    /// </summary>
    private static bool ReadsSecretFile(string step)
    {
        string[] words = Words(step);
        if (!words.Any(IsSecretFile))
        {
            return false;
        }

        for (int index = 0; index < words.Length; index++)
        {
            if (ReadsSecretFileArgument(words, index))
            {
                return true;
            }
        }

        return ReadsThroughInputRedirect(step) || ScriptOpensSecretFile(step);
    }

    /// <summary>Whether the program at this word reads a secret file among the arguments that follow it.</summary>
    private static bool ReadsSecretFileArgument(string[] words, int index)
    {
        string program = ProgramName(words[index]);
        if (!ReadingPrograms.Contains(program))
        {
            return false;
        }

        if (PatternFirstPrograms.Contains(program))
        {
            return AnySecretFileAfterPattern(words, index);
        }

        if (string.Equals(program, "dd", StringComparison.OrdinalIgnoreCase))
        {
            return words[(index + 1)..].Any(word =>
                word.StartsWith("if=", StringComparison.OrdinalIgnoreCase) && IsSecretFile(word[3..]));
        }

        if (string.Equals(program, "select-string", StringComparison.OrdinalIgnoreCase))
        {
            return SelectStringPaths(words, index).Any(IsSecretFile);
        }

        // cp and scp read the files they name except the last, which is where they write.
        int last = IsCopyProgram(program) ? words.Length - 1 : words.Length;
        for (int argument = index + 1; argument < last; argument++)
        {
            if (IsSecretFile(words[argument]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a program whose first argument is a pattern, script or filter reads a secret file among the
    /// arguments after that first one.
    /// </summary>
    private static bool AnySecretFileAfterPattern(string[] words, int index)
    {
        int first = index + 1;
        while (first < words.Length && words[first].StartsWith('-'))
        {
            first++;
        }

        for (int argument = first + 1; argument < words.Length; argument++)
        {
            if (IsSecretFile(words[argument]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The paths a PowerShell <c>Select-String</c> call names after <c>-Path</c> or <c>-LiteralPath</c>.</summary>
    private static IEnumerable<string> SelectStringPaths(string[] words, int index)
    {
        for (int argument = index + 1; argument < words.Length - 1; argument++)
        {
            if (string.Equals(words[argument], "-Path", StringComparison.OrdinalIgnoreCase)
                || string.Equals(words[argument], "-LiteralPath", StringComparison.OrdinalIgnoreCase))
            {
                yield return words[argument + 1];
            }
        }
    }

    /// <summary>Whether a single <c>&lt;</c> redirect feeds a secret file to this step.</summary>
    private static bool ReadsThroughInputRedirect(string step) =>
        InputRedirect().Matches(step).Any(match => IsSecretFile(match.Groups["path"].Value));

    /// <summary>Whether a script's <c>open(...)</c> call names a secret file as its argument.</summary>
    private static bool ScriptOpensSecretFile(string step) =>
        ScriptsOpenAFile().Matches(step).Any(match => IsSecretFile(match.Groups["path"].Value.Trim()));

    /// <summary>Whether this step prints a secret from a store: a secret manager, a process environment, or a
    /// variable whose name says it holds one.</summary>
    private static bool PrintsStoredSecret(string step)
    {
        if (UserSecretsList().IsMatch(step)
            || GitHubToken().IsMatch(step)
            || AzureToken().IsMatch(step)
            || EnvironmentListing().IsMatch(step)
            || ProcessEnvironment().IsMatch(step)
            || KeychainLookup().IsMatch(step) && KeychainPrintOption().IsMatch(step)
            || KubectlSecret().IsMatch(step) && KubectlOutput().IsMatch(step))
        {
            return true;
        }

        if (DockerInspect().IsMatch(step))
        {
            Match format = FormatOption().Match(step);
            string template = format.Success
                ? format.Groups["doubly"].Value
                    + format.Groups["singly"].Value
                    + format.Groups["bare"].Value
                : string.Empty;
            if (!format.Success || template.Contains("env", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (ScriptRuntime().IsMatch(step) && WholeEnvironment().IsMatch(step))
        {
            return true;
        }

        if (PrintingWord().IsMatch(step) && SecretVariable().IsMatch(step))
        {
            return true;
        }

        return PrintsWholeEnvironment(step);
    }

    /// <summary>
    /// Whether this step runs <c>env</c> or <c>printenv</c> with nothing to print, or prints one secret-named
    /// variable. Only a word where a command can stand counts, so <c>echo env</c> is not mistaken for one.
    /// </summary>
    private static bool PrintsWholeEnvironment(string step)
    {
        string[] words = Words(step);
        for (int index = 0; index < words.Length; index++)
        {
            string program = ProgramName(words[index]);
            if (!IsEnvironmentProgram(program) || !IsCommandPosition(words, index))
            {
                continue;
            }

            if (index == words.Length - 1)
            {
                return true;
            }

            if (string.Equals(program, "printenv", StringComparison.OrdinalIgnoreCase)
                && IsSecretName(words[index + 1]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this step puts a literal secret on the command line, in a variable, a tool option or a
    /// connection string.</summary>
    private static bool PutsLiteralSecret(string step)
    {
        foreach (Match assignment in Assignment().Matches(step))
        {
            if (IsSecretConnectionName(assignment.Groups["name"].Value)
                && IsLiteral(assignment.Groups["value"].Value))
            {
                return true;
            }
        }

        if (Regex.IsMatch(step, @"\bsqlcmd\b", RegexOptions.IgnoreCase)
            && SqlcmdPassword().Match(step) is { Success: true } sqlcmd
            && IsLiteral(sqlcmd.Groups["value"].Value))
        {
            return true;
        }

        if (Regex.IsMatch(step, @"\bmysql\b", RegexOptions.IgnoreCase)
            && MySqlPassword().Match(step) is { Success: true } mysql
            && IsLiteral(mysql.Groups["value"].Value))
        {
            return true;
        }

        if (Regex.IsMatch(step, @"\bcurl\b", RegexOptions.IgnoreCase)
            && CurlUser().Match(step) is { Success: true } curl)
        {
            string[] parts = curl.Groups["value"].Value.Split(':', 2);
            if (parts.Length == 2 && IsLiteral(parts[1]))
            {
                return true;
            }
        }

        return BearerToken().IsMatch(step)
            && IsLiteral(BearerToken().Match(step).Groups["value"].Value);
    }

    /// <summary>
    /// Whether a word names a secret file: a file whose name says it holds one, a dotenv file, a file with a key or
    /// certificate extension, a credential store, or anything under a private directory. The name fragment test
    /// covers a file with no extension or a configuration or data extension, so a source or documentation file such
    /// as <c>TokenService.cs</c> or <c>secret-handling.md</c> is not refused for its name. A listed directory, a
    /// published key and a dotenv example are not secret files.
    /// </summary>
    internal static bool IsSecretFile(string word)
    {
        string path = word.Trim('"', '\'', '`', ',', ';', '&', '|', '(', ')', '<', '>', '[', ']');
        if (path.Length == 0)
        {
            return false;
        }

        string[] segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        string file = segments[^1];
        if (SecretFileNames.Contains(file)
            || IsSecretFileName(file)
            || SecretExtensions.Any(extension => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.Equals(file, ".env", StringComparison.OrdinalIgnoreCase)
            || (file.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) && !ExampleDotEnvFiles.Contains(file)))
        {
            return true;
        }

        if (UnderDirectory(segments, ".microsoft", "usersecrets")
            || UnderDirectory(segments, ".azure")
            || EndsWithFile(segments, ".docker", "config.json")
            || EndsWithFile(segments, ".kube", "config"))
        {
            return true;
        }

        int ssh = Array.FindIndex(segments, segment => string.Equals(segment, ".ssh", StringComparison.OrdinalIgnoreCase));
        return ssh >= 0 && ssh < segments.Length - 1 && !IsPublicSshFile(file);
    }

    /// <summary>
    /// Whether the file's name alone says it holds a secret. The fragment test applies only to a file with no
    /// extension or a configuration or data extension.
    /// </summary>
    private static bool IsSecretFileName(string file)
    {
        int dot = file.LastIndexOf('.');
        string extension = dot > 0 ? file[dot..] : string.Empty;
        bool mayHoldConfiguration = extension.Length == 0
            || ConfigurationAndDataExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        return mayHoldConfiguration
            && SecretNameFragments.Any(fragment => file.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the path names a file that sits somewhere below the named directory.</summary>
    private static bool UnderDirectory(string[] segments, params string[] directory)
    {
        for (int start = 0; start + directory.Length < segments.Length; start++)
        {
            bool matches = true;
            for (int offset = 0; offset < directory.Length; offset++)
            {
                if (!string.Equals(segments[start + offset], directory[offset], StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the path ends in a named file immediately below a named directory.</summary>
    private static bool EndsWithFile(string[] segments, string directory, string file) =>
        segments.Length >= 2
        && string.Equals(segments[^2], directory, StringComparison.OrdinalIgnoreCase)
        && string.Equals(segments[^1], file, StringComparison.OrdinalIgnoreCase);

    private static bool IsPublicSshFile(string file) =>
        file.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)
        || string.Equals(file, "known_hosts", StringComparison.OrdinalIgnoreCase)
        || string.Equals(file, "config", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a value is a literal secret rather than a reference to one, which never enters the transcript.</summary>
    private static bool IsLiteral(string value)
    {
        string literal = value.Trim().Trim('"', '\'').TrimEnd(';', ',', ')', '}', ']');
        return literal.Length > 0 && !literal.StartsWith('$') && !literal.StartsWith('%');
    }

    /// <summary>Whether a name says it holds a secret.</summary>
    private static bool IsSecretName(string name) =>
        SecretNameFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a name says it holds a secret, including a connection string's <c>Pwd</c>.</summary>
    private static bool IsSecretConnectionName(string name) =>
        IsSecretName(name) || string.Equals(name, "pwd", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a program copies files, so its last argument is where it writes rather than reads.</summary>
    private static bool IsCopyProgram(string program) =>
        string.Equals(program, "cp", StringComparison.OrdinalIgnoreCase)
        || string.Equals(program, "scp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a program prints the environment.</summary>
    private static bool IsEnvironmentProgram(string program) =>
        string.Equals(program, "env", StringComparison.OrdinalIgnoreCase)
        || string.Equals(program, "printenv", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a word stands where a command can, so <c>env</c> as prose is not read as one.</summary>
    private static bool IsCommandPosition(string[] words, int index) =>
        index == 0
        || CommandWrappers.Contains(words[index - 1])
        || (words.Length > 0 && string.Equals(ProgramName(words[0]), "docker", StringComparison.Ordinal));

    /// <summary>The program a word names, with its directory and file extension of Windows kept off for matching.</summary>
    private static string ProgramName(string word)
    {
        string program = word.Trim('"', '\'', '`', ',', ';', '&', '|', '(', ')', '<', '>');
        int separator = program.LastIndexOfAny(['/', '\\']);
        return separator >= 0 ? program[(separator + 1)..] : program;
    }

    /// <summary>
    /// The command's words with quotes read as separators, so text inside a quote or a nested shell string is
    /// matched like any other text.
    /// </summary>
    private static string[] Words(string step) =>
        [.. step.Replace('"', ' ').Replace('\'', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('(', ')', '{', '}', '`', ',', ';', '&', '|', '<', '>', '!'))
            .Where(word => word.Length > 0)];

    /// <summary>
    /// The command split into the steps a shell would run: a separator outside quotes ends one, and a separator
    /// inside a quote belongs to the text a nested shell, a query or a value carries.
    /// </summary>
    private static IEnumerable<string> Steps(string command)
    {
        List<string> steps = [];
        StringBuilder step = new();
        char quote = '\0';
        for (int index = 0; index < command.Length; index++)
        {
            char current = command[index];
            if (quote != '\0')
            {
                step.Append(current);
                if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                step.Append(current);
                continue;
            }

            if (current is ';' or '\n' or '\r')
            {
                steps.Add(step.ToString());
                step.Clear();
                continue;
            }

            if (current is '&' or '|')
            {
                steps.Add(step.ToString());
                step.Clear();
                if (index + 1 < command.Length && command[index + 1] == current)
                {
                    index++;
                }

                continue;
            }

            step.Append(current);
        }

        steps.Add(step.ToString());
        return steps.Where(candidate => candidate.Trim().Length > 0);
    }

    /// <summary>The three cases the rule refuses, plus the unreadable command it fails closed on.</summary>
    internal enum SecretCase
    {
        /// <summary>A command printed a secret file's contents.</summary>
        ReadsSecretFile,

        /// <summary>A command printed a secret the environment or a store holds.</summary>
        PrintsStoredSecret,

        /// <summary>A command carried a secret's literal value.</summary>
        PutsLiteralSecret,

        /// <summary>A command's text could not be read, so the rule refuses rather than guesses.</summary>
        UnreadableCommand,
    }

    [GeneratedRegex(@"(?i)\bdotnet\s+user-secrets\s+list\b")]
    private static partial Regex UserSecretsList();

    [GeneratedRegex(@"(?i)\bgh\s+auth\s+token\b")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"(?i)\baz\s+account\s+get-access-token\b")]
    private static partial Regex AzureToken();

    [GeneratedRegex(@"(?i)\b(?:get-childitem|gci|dir)\s+env:")]
    private static partial Regex EnvironmentListing();

    [GeneratedRegex(@"(?i)\b(?:cat|head|tail|less|more|strings|xxd|base64)\b[^|]*?/proc/\S*environ")]
    private static partial Regex ProcessEnvironment();

    [GeneratedRegex(@"(?i)\bsecurity\s+(?:find-generic-password|find-internet-password)\b")]
    private static partial Regex KeychainLookup();

    [GeneratedRegex(@"(?:^|\s)-[wg](?:\s|$)")]
    private static partial Regex KeychainPrintOption();

    [GeneratedRegex(@"(?i)\bkubectl\s+get\s+secrets?\b")]
    private static partial Regex KubectlSecret();

    [GeneratedRegex(@"(?i)(?:-o|--output)(?:\s+|=)(?:yaml|json|jsonpath)")]
    private static partial Regex KubectlOutput();

    [GeneratedRegex(@"(?i)\bdocker\s+inspect\b")]
    private static partial Regex DockerInspect();

    [GeneratedRegex(@"(?i)(?:--format|-f)(?:\s+|=)(""(?<doubly>[^""]*)""|'(?<singly>[^']*)'|(?<bare>\S+))")]
    private static partial Regex FormatOption();

    [GeneratedRegex(@"(?i)\b(?:python[0-9.]*|py|node)\b")]
    private static partial Regex ScriptRuntime();

    [GeneratedRegex(@"os\.environ|process\.env")]
    private static partial Regex WholeEnvironment();

    [GeneratedRegex(@"(?<!<)<(?!<)\s*(?<path>[^\s<>]+)")]
    private static partial Regex InputRedirect();

    [GeneratedRegex(@"(?i)\bopen\s*\(\s*(?<path>[^,)\r\n]*)")]
    private static partial Regex ScriptsOpenAFile();

    [GeneratedRegex(@"(?i)\b(?:echo|printf|write-output|write-host)\b")]
    private static partial Regex PrintingWord();

    /// <summary>A variable reference whose name says it holds a secret.</summary>
    [GeneratedRegex(@"\$\{?(?:env:)?[A-Za-z_][A-Za-z0-9_]*(?:PASSWORD|PASSWD|SECRET|TOKEN|APIKEY|API_KEY|CREDENTIAL)[A-Za-z0-9_]*\}?")]
    private static partial Regex SecretVariable();

    /// <summary>Every <c>NAME=value</c> the step carries, which is a bare assignment or a tool's <c>-e</c> option.</summary>
    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_]*)=(?<value>[^\s;]*)")]
    private static partial Regex Assignment();

    [GeneratedRegex(@"(?:^|\s)-P\s+(?<value>\S+)")]
    private static partial Regex SqlcmdPassword();

    [GeneratedRegex(@"(?:^|\s)-p(?<value>[^\s-]\S*)")]
    private static partial Regex MySqlPassword();

    [GeneratedRegex(@"(?:^|\s)(?:-u|--user)(?:\s+|=)?(?<value>[^\s]+)")]
    private static partial Regex CurlUser();

    [GeneratedRegex(@"(?i)authorization:\s*bearer\s+(?<value>\S+)")]
    private static partial Regex BearerToken();
}
