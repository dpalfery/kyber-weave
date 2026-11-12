using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The architect's shell boundary (<c>KW-ARB-SHELL-001</c>): a stateless check that allows a
/// <c>Bash</c> or <c>PowerShell</c> call only when every simple command in it is on the architect's
/// documented list.
/// </summary>
/// <remarks>
/// The rule is permanent and not configurable, so it lives beside <see cref="ReadGuard"/> and
/// <see cref="ConductorGuard"/> rather than in the rules file: a project cannot switch it off. It is
/// enforced at once, with no shadow mode. Matching is whole-command and anchored per form, never a
/// substring match, so a command that merely contains a listed word is not a listed form. The call is
/// split on unquoted separators; each segment is tokenised, refused on any construct the list cannot
/// account for, and matched against the form table. Anything that cannot be tokenised is denied, and
/// every denial names the segment as written together with the replacement to use instead.
/// <see cref="DocumentedForms"/> is the table the architect's agent file must document; the sync test
/// keeps the two from drifting apart.
/// </remarks>
public static class ArchitectShellGuard
{
    /// <summary>The permanent rule id. Rule ids are never reused or renumbered.</summary>
    public const string RuleId = "KW-ARB-SHELL-001";

    /// <summary>The segment named in a denial when the call carries no command string.</summary>
    private const string NoCommandSegment = "<no command>";

    private static readonly IReadOnlySet<string> GuardedProfiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "architect",
    };

    /// <summary>Commands that run another program, so no form of them can be checked by what they run.</summary>
    private static readonly IReadOnlySet<string> WrapperCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "bash", "sh", "zsh", "pwsh", "powershell", "eval", "exec", "xargs", "env", "command", "sudo", "nohup",
        "time", "source", ".",
    };

    private static readonly IReadOnlySet<string> InterpreterCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "python", "python3", "py", "node", "ruby", "perl", "php", "deno", "bun",
    };

    private static readonly IReadOnlySet<string> ShellKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "for", "while", "until", "if", "then", "do", "done", "case", "function", "select",
    };

    private static readonly IReadOnlySet<string> FindActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "-exec", "-execdir", "-ok", "-okdir", "-delete", "-fprint", "-fprint0", "-fprintf", "-fls",
    };

    private static readonly IReadOnlySet<string> GitReadSubcommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "log", "diff", "status", "show", "rev-parse",
    };

    private static readonly char[] GlobCharacters = ['*', '?', '[', '{'];

    private static readonly Refusal SubstitutionRefusal = new(
        "uses a variable or command substitution, which is not allowed",
        "Pass literal values, one listed command per segment");

    private static readonly Refusal GroupingRefusal = new(
        "groups commands or opens a subshell, which is not allowed",
        "Run the commands one at a time as separate listed forms");

    private static readonly Refusal BackgroundRefusal = new(
        "runs a command in the background, which is not allowed",
        "Run the command in the foreground and wait for its result");

    private static readonly Refusal BackslashRefusal = new(
        "uses a backslash escape, which is not allowed in Bash",
        "Quote the argument instead of escaping it");

    private static readonly Refusal InputRedirectRefusal = new(
        "reads its input through a redirect, which is not allowed",
        "Use the Read tool, with offset and limit, to read the file");

    private static readonly Refusal OutputRedirectRefusal = new(
        "writes output through a redirect, which is not allowed",
        "Return the text in your reply, or use the edit tools to write a file inside the write boundary");

    private static readonly Refusal UnbalancedQuoteRefusal = new(
        "has unbalanced quotes, so it cannot be read safely",
        "Close the quote, or quote the argument correctly");

    /// <summary>
    /// The documented forms: each backticked span the architect's command-set clause names, with one
    /// sample command the guard must answer with the verdict recorded here. The keys are the clause text,
    /// so a form the document names and the guard does not know, or the reverse, fails the sync test.
    /// </summary>
    public static IReadOnlyDictionary<string, DocumentedForm> DocumentedForms { get; } =
        new Dictionary<string, DocumentedForm>(StringComparer.Ordinal)
        {
            ["kyber-weave docs validate . --merge-ready"] = new("kyber-weave docs validate . --merge-ready", true),
            ["kyber-weave docs drift ."] = new("kyber-weave docs drift .", true),
            ["codegraph explore \"…\""] = new("codegraph explore \"x\"", true),
            ["kyber-weave doctor --ensure"] = new("kyber-weave doctor --ensure", true),
            ["git"] = new("git status", true),
            ["log"] = new("git log -1", true),
            ["diff"] = new("git diff main...HEAD", true),
            ["status"] = new("git status", true),
            ["show"] = new("git show HEAD:path", true),
            ["rev-parse"] = new("git rev-parse --git-common-dir", true),
            ["grep"] = new("grep -rn Foo src", true),
            ["rg"] = new("rg -n Foo src", true),
            ["ls"] = new("ls docs/plans", true),
            ["find"] = new("find docs -name '*.md'", true),
            ["-exec"] = new("find . -exec ls {} ;", false),
            ["-ok"] = new("find . -ok ls {} ;", false),
            ["-delete"] = new("find . -delete", false),
            ["head"] = new("git log | head -5", true),
            ["tail"] = new("git log | tail -5", true),
            ["wc"] = new("grep -c x f | wc -l", true),
            ["cut"] = new("cut -d, -f1 f", true),
            ["sort"] = new("sort f", true),
            ["uniq"] = new("grep -c x f | sort | uniq", true),
            ["cd <dir>"] = new("cd /w && git status", true),
            ["&&"] = new("git status && git log -1", true),
            ["touch <file>"] = new("touch docs/plans/x.md", true),
        };

    /// <summary>Whether <paramref name="agentName"/> is an agent whose shell calls the architect guard checks.</summary>
    /// <param name="agentName">The agent name the hook resolved, or null when the caller is unknown.</param>
    /// <returns>True only for an agent in the <c>architect</c> capability profile.</returns>
    public static bool IsShellGuarded(string? agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            return false;
        }

        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();
        return catalog.Agents.TryGetValue(agentName, out ArbiterSquadAgent? agent)
            && GuardedProfiles.Contains(agent.CapabilityProfile);
    }

    /// <summary>
    /// Checks one <c>Bash</c> or <c>PowerShell</c> call against the architect's documented list.
    /// </summary>
    /// <param name="toolName">The tool the call names: <c>Bash</c> or <c>PowerShell</c>.</param>
    /// <param name="toolInput">The tool input. Its <c>command</c> string is the call; a missing or non-string command is denied.</param>
    /// <param name="workingDirectory">The payload's working directory, where relative paths start.</param>
    /// <param name="planningDirectories">The directories <c>touch</c> may create files beneath.</param>
    /// <returns>The verdict. A denial names the offending segment as written, and the replacement.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="toolName"/> is neither <c>Bash</c> nor <c>PowerShell</c>.</exception>
    public static ShellGuardResult Check(
        string toolName,
        JsonElement toolInput,
        string workingDirectory,
        IReadOnlyList<string> planningDirectories)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(planningDirectories);

        bool bash = string.Equals(toolName, "Bash", StringComparison.Ordinal);
        if (!bash && !string.Equals(toolName, "PowerShell", StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(toolName),
                toolName,
                "Only Bash and PowerShell calls are checked by the shell guard.");
        }

        string? command = CommandOf(toolInput);
        if (command is null)
        {
            return Deny(toolName, NoCommandSegment, new Refusal(
                "has no command string to check",
                "Send the command as a string in the command field"));
        }

        string directory = workingDirectory;
        foreach (Segment segment in Split(command, bash))
        {
            Refusal? refusal = CheckSegment(segment, bash, ref directory, planningDirectories);
            if (refusal is not null)
            {
                return Deny(toolName, segment.Text, refusal);
            }
        }

        return ShellGuardResult.Allow;
    }

    private static ShellGuardResult Deny(string toolName, string segment, Refusal refusal) =>
        new(
            false,
            $"Kyber-Weave Arbiter shell guard ({RuleId}) denied this {toolName} call: '{segment}' " +
            $"{refusal.Why}. {refusal.Replacement}. If you need this command, return STATUS: BLOCKED " +
            "naming it; do not work around the denial.");

    private static string? CommandOf(JsonElement toolInput)
    {
        if (toolInput.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return toolInput.TryGetProperty("command", out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    // ---- Splitting ---------------------------------------------------------------------------------

    /// <summary>
    /// Splits the call on unquoted <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c> and newline. A backslash
    /// (Bash) or backtick keeps the character after it in the same segment, so an escaped separator is never
    /// a split point. A lone <c>&amp;</c> stays in its segment, where the tokeniser refuses it.
    /// </summary>
    private static List<Segment> Split(string command, bool bash)
    {
        List<Segment> segments = [];
        StringBuilder current = new();
        Quote quote = Quote.None;

        void Close(string? separator)
        {
            segments.Add(new Segment(current.ToString().Trim(), separator));
            current.Clear();
        }

        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (quote == Quote.Single)
            {
                current.Append(c);
                if (c == '\'')
                {
                    quote = Quote.None;
                }

                continue;
            }

            if (quote == Quote.Double)
            {
                current.Append(c);
                if (c == '\\' && bash && i + 1 < command.Length)
                {
                    current.Append(command[++i]);
                }
                else if (c == '"')
                {
                    quote = Quote.None;
                }

                continue;
            }

            if ((c == '\\' && bash) || c == '`')
            {
                current.Append(c);
                if (i + 1 < command.Length)
                {
                    current.Append(command[++i]);
                }

                continue;
            }

            char next = i + 1 < command.Length ? command[i + 1] : '\0';
            switch (c)
            {
                case '\'':
                    quote = Quote.Single;
                    current.Append(c);
                    break;
                case '"':
                    quote = Quote.Double;
                    current.Append(c);
                    break;
                case '\n':
                    Close("\n");
                    break;
                case ';':
                    Close(";");
                    break;
                case '|' when next == '|':
                    i++;
                    Close("||");
                    break;
                case '|':
                    Close("|");
                    break;
                case '&' when next == '&':
                    i++;
                    Close("&&");
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        Close(null);
        return segments;
    }

    // ---- Tokenising --------------------------------------------------------------------------------

    /// <summary>
    /// Reads one segment into words with quotes removed. Any construct the list cannot account for is
    /// refused here, before the command is matched: substitutions, grouping, backgrounding, a redirect other
    /// than <c>2&gt;&amp;1</c> and (Bash) <c>2&gt;/dev/null</c>, a backslash in Bash, and unbalanced quotes.
    /// </summary>
    private static Tokens Tokenize(string text, bool bash)
    {
        List<string> words = [];
        StringBuilder word = new();
        bool inWord = false;
        bool quoted = false;
        Quote quote = Quote.None;

        void Flush()
        {
            if (inWord)
            {
                words.Add(word.ToString());
                word.Clear();
                inWord = false;
                quoted = false;
            }
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote == Quote.Single)
            {
                if (c == '\'')
                {
                    quote = Quote.None;
                }
                else
                {
                    word.Append(c);
                }

                continue;
            }

            if (quote == Quote.Double)
            {
                if (c == '"')
                {
                    quote = Quote.None;
                    continue;
                }

                if (c == '$' || c == '`')
                {
                    return new Tokens(words, SubstitutionRefusal);
                }

                if (c == '\\' && bash && i + 1 < text.Length)
                {
                    char escaped = text[i + 1];
                    if (escaped == '$' || escaped == '`')
                    {
                        return new Tokens(words, SubstitutionRefusal);
                    }

                    word.Append(escaped);
                    i++;
                    continue;
                }

                word.Append(c);
                continue;
            }

            if (c == ' ' || c == '\t' || c == '\r')
            {
                Flush();
                continue;
            }

            switch (c)
            {
                case '\'':
                    quote = Quote.Single;
                    inWord = true;
                    quoted = true;
                    break;
                case '"':
                    quote = Quote.Double;
                    inWord = true;
                    quoted = true;
                    break;
                case '$':
                case '`':
                    return new Tokens(words, SubstitutionRefusal);
                case '(':
                case ')':
                case '{':
                case '}':
                    return new Tokens(words, GroupingRefusal);
                case '<':
                    return new Tokens(words, InputRedirectRefusal);
                case '&':
                    return new Tokens(words, BackgroundRefusal);
                case '\\' when bash:
                    return new Tokens(words, BackslashRefusal);
                case '>':
                    if (inWord && !quoted && word.ToString() == "2" && AllowedErrorRedirect(text, i + 1, bash, out int last))
                    {
                        // The descriptor and the redirect are one token that the list allows; it names no file.
                        word.Clear();
                        inWord = false;
                        i = last;
                        break;
                    }

                    return new Tokens(words, OutputRedirectRefusal);
                default:
                    word.Append(c);
                    inWord = true;
                    break;
            }
        }

        if (quote != Quote.None)
        {
            return new Tokens(words, UnbalancedQuoteRefusal);
        }

        Flush();
        return new Tokens(words, null);
    }

    /// <summary>
    /// Whether the text after a <c>2&gt;</c> is exactly the allowed error redirect: <c>&amp;1</c> in either
    /// shell, or <c>/dev/null</c> in Bash. <paramref name="last"/> receives the index of its final character.
    /// </summary>
    private static bool AllowedErrorRedirect(string text, int start, bool bash, out int last)
    {
        last = start;
        if (string.CompareOrdinal(text, start, "&1", 0, 2) == 0 && IsSegmentEndAt(text, start + 2))
        {
            last = start + 1;
            return true;
        }

        if (bash && string.CompareOrdinal(text, start, "/dev/null", 0, 9) == 0 && IsSegmentEndAt(text, start + 9))
        {
            last = start + 8;
            return true;
        }

        return false;
    }

    private static bool IsSegmentEndAt(string text, int index) =>
        index >= text.Length || text[index] is ' ' or '\t' or '\r';

    // ---- Checking a segment ------------------------------------------------------------------------

    /// <summary>Checks one segment, returning the refusal that denies it or null when it is a listed form.</summary>
    private static Refusal? CheckSegment(
        Segment segment,
        bool bash,
        ref string directory,
        IReadOnlyList<string> planningDirectories)
    {
        if (segment.Text.Length == 0)
        {
            return new Refusal("is empty between separators", "Remove the stray separator");
        }

        Tokens tokens = Tokenize(segment.Text, bash);
        if (tokens.Refusal is not null)
        {
            return tokens.Refusal;
        }

        List<string> words = tokens.Words;
        if (words.Count == 0)
        {
            return new Refusal("names no command", "Put a listed command before any redirect");
        }

        string name = words[0];
        if (IsAssignment(name))
        {
            return new Refusal(
                "sets a variable before the command, which is not allowed",
                "Run the command without a leading assignment");
        }

        if (ShellKeywords.Contains(name))
        {
            return new Refusal(
                "is a shell control keyword, which is not a listed form",
                "Run each listed command as its own segment, without loops or conditionals");
        }

        if (string.Equals(name, "cd", StringComparison.Ordinal))
        {
            return CheckChangeDirectory(segment, bash, words, ref directory);
        }

        return CheckCommand(name, words, directory, planningDirectories);
    }

    private static bool IsAssignment(string word)
    {
        int equals = word.IndexOf('=', StringComparison.Ordinal);
        return equals > 0
            && !char.IsAsciiDigit(word[0])
            && word[..equals].All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    /// <summary>
    /// Checks <c>cd &lt;dir&gt;</c>: one operand, no flags, and a following <c>&amp;&amp;</c> (or <c>;</c> in
    /// PowerShell) so the directory it sets applies to the commands after it.
    /// </summary>
    private static Refusal? CheckChangeDirectory(Segment segment, bool bash, List<string> words, ref string directory)
    {
        if (words.Count != 2)
        {
            return new Refusal("needs exactly one directory operand", "Name one directory: cd <dir>");
        }

        if (IsFlag(words[1]))
        {
            return new Refusal("uses a flag or cd -, which is not allowed", "Name the directory itself: cd <dir>");
        }

        bool chained = string.Equals(segment.Separator, "&&", StringComparison.Ordinal)
            || (!bash && string.Equals(segment.Separator, ";", StringComparison.Ordinal));
        if (!chained)
        {
            return new Refusal(
                "changes directory without a following && command",
                "Put the cd first, joined to the command it serves with &&");
        }

        string? resolved = Resolve(directory, words[1]);
        if (resolved is null)
        {
            return UnresolvableRefusal;
        }

        directory = resolved;
        return null;
    }

    private static Refusal? CheckCommand(
        string name,
        List<string> words,
        string directory,
        IReadOnlyList<string> planningDirectories)
    {
        if (WrapperCommands.Contains(name))
        {
            return new Refusal(
                "runs another program through a wrapper, which is not a listed form",
                "Run the program itself, without a wrapper");
        }

        if (InterpreterCommands.Contains(name))
        {
            return new Refusal(
                "runs an interpreter or script, which is not allowed",
                "Ask the conductor to run that script or tool");
        }

        switch (name)
        {
            case "kyber-weave":
                return CheckKyberWeave(words);
            case "codegraph":
                return words.Count >= 3 && string.Equals(words[1], "explore", StringComparison.Ordinal)
                    ? null
                    : new Refusal(
                        "is not a codegraph form on the architect's list",
                        "Use codegraph explore \"<query>\"");
            case "git":
                return CheckGit(words);
            case "grep":
            case "ls":
            case "head":
            case "tail":
            case "wc":
            case "cut":
                return null;
            case "rg":
                return CheckRipgrep(words);
            case "find":
                return CheckFind(words);
            case "sort":
                return CheckSort(words);
            case "uniq":
                return CheckUniq(words);
            case "touch":
                return CheckTouch(words, directory, planningDirectories);
            case "cat":
            case "sed":
            case "awk":
                return new Refusal(
                    "reads files with a command that is not on the architect's list",
                    "Use the Read tool, with offset and limit, to read the file");
            case "gh":
            case "curl":
            case "wget":
                return new Refusal(
                    "reaches the network, which is not allowed",
                    "Ask the conductor to relay the request");
            case "echo":
            case "printf":
            case "tee":
                return new Refusal(
                    "prints or writes text, which is not on the architect's list",
                    "Use the edit tools to write file content inside the write boundary, and reply with the text itself");
            case "pwd":
                return new Refusal(
                    "prints the working directory, which is not on the architect's list",
                    "The session already gives the working directory");
            default:
                return new Refusal(
                    "is not on the architect's command list",
                    "Use a listed command, or ask the conductor to run it");
        }
    }

    private static Refusal? CheckKyberWeave(List<string> words)
    {
        bool docs = words.Count >= 3 && string.Equals(words[1], "docs", StringComparison.Ordinal);
        bool validate = docs
            && words.Count == 5
            && string.Equals(words[2], "validate", StringComparison.Ordinal)
            && !IsFlag(words[3])
            && string.Equals(words[4], "--merge-ready", StringComparison.Ordinal);
        bool drift = docs
            && words.Count == 4
            && string.Equals(words[2], "drift", StringComparison.Ordinal)
            && !IsFlag(words[3]);
        bool doctor = words.Count >= 3
            && string.Equals(words[1], "doctor", StringComparison.Ordinal)
            && string.Equals(words[2], "--ensure", StringComparison.Ordinal)
            && (words.Count == 3 || (words.Count == 4 && !IsFlag(words[3])));

        return validate || drift || doctor
            ? null
            : new Refusal(
                "is not a kyber-weave form on the architect's list",
                "Use kyber-weave docs validate <dir> --merge-ready, docs drift <dir>, or doctor --ensure");
    }

    private static Refusal? CheckGit(List<string> words)
    {
        int index = 1;
        while (index < words.Count && IsFlag(words[index]))
        {
            if (string.Equals(words[index], "--no-pager", StringComparison.Ordinal))
            {
                index++;
            }
            else if (string.Equals(words[index], "-C", StringComparison.Ordinal) && index + 1 < words.Count)
            {
                index += 2;
            }
            else
            {
                return new Refusal(
                    "uses a git global option that is not allowed",
                    "Use git with only --no-pager or -C <dir> before the subcommand");
            }
        }

        if (index >= words.Count)
        {
            return new Refusal("names no git subcommand", "Name one of: git log, diff, status, show or rev-parse");
        }

        string subcommand = words[index];
        if (!GitReadSubcommands.Contains(subcommand))
        {
            return GitRefusal(subcommand);
        }

        for (int argument = index + 1; argument < words.Count; argument++)
        {
            if (IsOption(words[argument], "--output"))
            {
                return new Refusal(
                    "writes its output to a file, which is not allowed",
                    "Read the output from standard output");
            }
        }

        return null;
    }

    private static Refusal GitRefusal(string subcommand)
    {
        switch (subcommand)
        {
            case "grep":
            case "ls-files":
            case "blame":
                return new Refusal(
                    "searches or lists files through git, which is not on the architect's list",
                    "Use CodeGraph, grep or find to search the code");
            case "fetch":
                return new Refusal(
                    "reaches the network through git fetch, which is not allowed",
                    "The conductor fetches; ask it to");
            default:
                return new Refusal(
                    "is not a git form on the architect's list",
                    "Use git log, diff, status, show or rev-parse, or ask the conductor to run it");
        }
    }

    private static Refusal? CheckRipgrep(List<string> words)
    {
        foreach (string argument in words.Skip(1))
        {
            if (IsOption(argument, "--pre")
                || IsOption(argument, "--pre-glob")
                || IsOption(argument, "--search-zip")
                || HasShortFlag(argument, 'z'))
            {
                return new Refusal(
                    "runs a preprocessor or reads compressed files through rg, which is not allowed",
                    "Search the plain files with grep or rg without those options");
            }
        }

        return null;
    }

    private static Refusal? CheckFind(List<string> words)
    {
        foreach (string argument in words.Skip(1))
        {
            if (FindActions.Contains(argument))
            {
                return new Refusal(
                    "runs a command or writes a file through find, which is not allowed",
                    "Use find without action options, and read the matches from its output");
            }
        }

        return null;
    }

    private static Refusal? CheckSort(List<string> words)
    {
        foreach (string argument in words.Skip(1))
        {
            if (string.Equals(argument, "-o", StringComparison.Ordinal)
                || IsOption(argument, "--output")
                || IsOption(argument, "--compress-program")
                || HasShortFlag(argument, 'o'))
            {
                return new Refusal(
                    "writes to a file or runs a program through sort, which is not allowed",
                    "Read the sorted output from standard output");
            }
        }

        return null;
    }

    private static Refusal? CheckUniq(List<string> words)
    {
        return words.Skip(1).Count(argument => !IsFlag(argument)) > 1
            ? new Refusal(
                "names two files, and the second would be an output file, which is not allowed",
                "Pass at most one input file to uniq")
            : null;
    }

    /// <summary>
    /// Checks <c>touch</c>: literal operands with no flags, each resolved against the effective directory,
    /// beneath one of the planning directories, and named <c>.md</c> or <c>.json</c>.
    /// </summary>
    private static Refusal? CheckTouch(List<string> words, string directory, IReadOnlyList<string> planningDirectories)
    {
        if (words.Count < 2)
        {
            return new Refusal(
                "names no file to create",
                "Name one literal file path beneath the plan, specification or todo directory");
        }

        foreach (string operand in words.Skip(1))
        {
            if (IsFlag(operand))
            {
                return new Refusal(
                    "uses a flag, which touch is not allowed here",
                    "Run touch with literal file paths and no flags");
            }

            if (operand.IndexOfAny(GlobCharacters) >= 0)
            {
                return new Refusal("uses a glob pattern, which is not allowed", "Name each file literally");
            }

            string? path = Resolve(directory, operand);
            if (path is null)
            {
                return UnresolvableRefusal;
            }

            if (!IsBeneathPlanningDirectory(path, planningDirectories))
            {
                return new Refusal(
                    "names a file outside the plan, specification and todo directories",
                    "Create files only beneath the directories the index properties name");
            }

            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return new Refusal(
                    "names a file that is not .md or .json",
                    "Create only .md or .json planning files");
            }
        }

        return null;
    }

    private static readonly Refusal UnresolvableRefusal = new(
        "names a path the guard cannot resolve from its directory, which is not allowed",
        "Name a path beneath the working directory, without climbing above it");

    private static bool IsBeneathPlanningDirectory(string path, IReadOnlyList<string> planningDirectories)
    {
        foreach (string directory in planningDirectories)
        {
            string trimmed = directory.Replace('\\', '/').Trim('/');
            if (trimmed.Length > 0 && path.Contains("/" + trimmed + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Paths -------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves an operand against a directory and normalises it to one forward-slash spelling. Returns null
    /// when the result is not rooted or a <c>..</c> climbs above the root, because such a path has no
    /// spelling the planning check can trust.
    /// </summary>
    private static string? Resolve(string baseDirectory, string operand)
    {
        string forward = operand.Replace('\\', '/');
        string joined = IsRooted(forward)
            ? forward
            : $"{baseDirectory.Replace('\\', '/').TrimEnd('/')}/{forward}";
        return Normalize(joined);
    }

    private static bool IsRooted(string path) =>
        path.StartsWith('/') || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/');

    private static string? Normalize(string path)
    {
        string prefix = path.Length >= 2 && path[1] == ':' ? path[..2] : string.Empty;
        string remainder = path[prefix.Length..];
        if (!remainder.StartsWith('/'))
        {
            return null;
        }

        List<string> parts = [];
        foreach (string part in remainder.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return prefix + "/" + string.Join("/", parts);
    }

    // ---- Small predicates --------------------------------------------------------------------------

    private static bool IsFlag(string word) => word.StartsWith('-');

    private static bool IsOption(string argument, string option) =>
        string.Equals(argument, option, StringComparison.Ordinal)
        || argument.StartsWith(option + "=", StringComparison.Ordinal);

    private static bool HasShortFlag(string argument, char flag) =>
        argument.Length > 1 && argument[0] == '-' && argument[1] != '-' && argument.IndexOf(flag, 1) > 0;

    private enum Quote
    {
        None,
        Single,
        Double,
    }

    /// <summary>One simple command as written, with the separator that follows it (null for the last).</summary>
    private sealed record Segment(string Text, string? Separator);

    /// <summary>The words of one segment, and the refusal that stopped the read, if any.</summary>
    private sealed record Tokens(List<string> Words, Refusal? Refusal);

    /// <summary>Why a construct is refused, and the replacement the architect should use instead.</summary>
    private sealed record Refusal(string Why, string Replacement);
}

/// <summary>One shell guard verdict.</summary>
/// <param name="Allowed">True when every simple command in the call is on the architect's list.</param>
/// <param name="Reason">The one-line denial reason, when denied.</param>
public sealed record ShellGuardResult(bool Allowed, string? Reason)
{
    /// <summary>The allow verdict.</summary>
    public static ShellGuardResult Allow { get; } = new(true, null);
}

/// <summary>One documented form of the architect's command set.</summary>
/// <param name="SampleCommand">A concrete command the form covers, which the guard must answer with <paramref name="Allowed"/>.</param>
/// <param name="Allowed">Whether the architect may run the form.</param>
public sealed record DocumentedForm(string SampleCommand, bool Allowed);
