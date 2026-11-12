using System.Buffers;
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
/// <see cref="ConductorGuard"/> rather than in the rules file, so no rule can switch it off. It is
/// active only while the Arbiter is enabled: <c>arbiter.enabled: false</c> turns the whole Arbiter off, this guard with it. It is
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

    /// <summary>
    /// The product's command name. A constant rather than a literal case label: the shipped-surface
    /// policy scans tracked files for a quoted command name followed by a colon, and that shape is the
    /// retired-key form it exists to catch.
    /// </summary>
    private const string KyberWeaveCli = "kyber-weave";

    private static readonly IReadOnlySet<string> SortSafeLongFlags = new HashSet<string>(StringComparer.Ordinal)
    {
        "--reverse", "--numeric-sort", "--unique", "--key", "--field-separator", "--ignore-case",
        "--dictionary-order", "--general-numeric-sort", "--human-numeric-sort", "--version-sort", "--stable",
        "--ignore-leading-blanks", "--ignore-nonprinting", "--month-sort", "--random-sort",
    };

    /// <summary>
    /// The rg options that run a program or read compressed input through one: a preprocessor, a
    /// hostname command, or a hyperlink format. Matched exactly, or by prefix for the families.
    /// </summary>
    private static readonly IReadOnlySet<string> RipgrepExecutingOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "--pre", "--pre-glob", "--search-zip", "--hostname-bin", "--hyperlink-format",
    };

    /// <summary>The git options that write a file or run a program, named without their leading dashes.</summary>
    private static readonly string[] GitDeniedLongOptions = ["output", "ext-diff", "textconv"];

    /// <summary>
    /// The git diff options whose names begin with <c>output</c> but only set how a diff is printed. They are
    /// exempt from the prefix rule, so they stay allowed. Matched exactly, without their <c>=value</c>.
    /// </summary>
    private static readonly IReadOnlySet<string> GitBenignOutputOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "output-indicator-new", "output-indicator-old", "output-indicator-context",
    };

    private static readonly IReadOnlySet<string> GuardedProfiles = new HashSet<string>(StringComparer.Ordinal)
    {
        ArbiterSquadCatalog.ArchitectCapabilityProfile,
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

    /// <summary>
    /// The characters PowerShell reads as a quote or a statement break beyond ASCII: the typographic single and
    /// double quotes (U+2018 to U+201F), the carriage return, and the next-line, line- and paragraph-separator
    /// characters (U+0085, U+2028, U+2029). A PowerShell call that holds one is refused.
    /// </summary>
    private static readonly SearchValues<char> PowerShellQuoteAndBreakCharacters = SearchValues.Create(
    [
        '\u2018', '\u2019', '\u201A', '\u201B', '\u201C', '\u201D', '\u201E', '\u201F', '\r', '\u0085', '\u2028', '\u2029',
    ]);

    /// <summary>
    /// The uniq options that take a value as the next word (<c>-f N</c>, <c>-s N</c>, <c>-w N</c>), named in long form.
    /// </summary>
    private static readonly IReadOnlySet<string> UniqValueLongOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "--skip-fields", "--skip-chars", "--check-chars",
    };

    private static readonly Refusal CarriageReturnRefusal = new(
        "contains a carriage return outside quotes, which ends a command in PowerShell and is not allowed",
        "Put each command in its own call, without line breaks");

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
            ["kyber-weave arbiter plan <plan file> -f json"] = new("kyber-weave arbiter plan docs/plans/x.md -f json", true),
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

        // The name is matched without regard to case: a caller that names the architect in any casing is the architect.
        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();
        foreach (KeyValuePair<string, ArbiterSquadAgent> entry in catalog.Agents)
        {
            if (string.Equals(entry.Key, agentName, StringComparison.OrdinalIgnoreCase))
            {
                return GuardedProfiles.Contains(entry.Value.CapabilityProfile);
            }
        }

        return false;
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

        // The payload's directory is the origin: touch creates files only beneath the planning directories of
        // this tree, however far a cd has moved the effective directory. A blank or unrooted origin is null, and
        // touch then fails closed rather than anchoring to the file-system root.
        string forward = workingDirectory.Replace('\\', '/');
        string? origin = IsRooted(forward) ? Normalize(forward) : null;
        string directory = origin ?? forward;
        string? departure = null;
        string? previous = null;
        foreach (Segment segment in Split(command, bash))
        {
            Refusal? refusal = CheckSegment(segment, bash, previous, ref directory, ref departure, origin, planningDirectories);
            if (refusal is not null)
            {
                return Deny(toolName, refusal.Segment ?? segment.Text, refusal);
            }

            previous = segment.Separator;
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
            // Only blanks are trimmed: a carriage return or a PowerShell line break inside the text must stay visible to
            // the checks that refuse it, not vanish at a segment edge.
            segments.Add(new Segment(current.ToString().Trim(' ', '\t'), separator));
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
                case ';' when EndsWithBraceWord(current):
                    // find's own terminator after {} (find . -exec ls {} ;) is not a separator. The segment it ends
                    // holds a brace, which the tokeniser always refuses, so the whole chunk is denied as written.
                    current.Append(c);
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

    /// <summary>Whether the text read so far ends with a bare <c>{}</c> word.</summary>
    private static bool EndsWithBraceWord(StringBuilder current)
    {
        string text = current.ToString().TrimEnd();
        return text.EndsWith("{}", StringComparison.Ordinal)
            && (text.Length == 2 || char.IsWhiteSpace(text[text.Length - 3]));
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
        List<bool> quotedWords = [];
        StringBuilder word = new();
        bool inWord = false;
        bool quoted = false;
        Quote quote = Quote.None;

        void Flush()
        {
            if (inWord)
            {
                words.Add(word.ToString());
                quotedWords.Add(quoted);
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

            if (c == ' ' || c == '\t')
            {
                Flush();
                continue;
            }

            switch (c)
            {
                case '\r':
                    // Bash reads a bare carriage return as a word character, and PowerShell as a statement break;
                    // neither reading is one the list can account for.
                    return new Tokens(words, CarriageReturnRefusal);
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
        return new Tokens(words, null, quotedWords);
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
        string? previousSeparator,
        ref string directory,
        ref string? departure,
        string? origin,
        IReadOnlyList<string> planningDirectories)
    {
        if (segment.Text.Length == 0)
        {
            return new Refusal("is empty between separators", "Remove the stray separator");
        }

        // PowerShell reads these as quotes or statement breaks, and the tokeniser reads them as ordinary characters,
        // so a segment holding one could mean something the list never saw. They are refused before tokenising.
        if (!bash)
        {
            int at = segment.Text.AsSpan().IndexOfAny(PowerShellQuoteAndBreakCharacters);
            if (at >= 0)
            {
                return new Refusal(
                    $"contains U+{(int)segment.Text[at]:X4}, which PowerShell reads as a quote or a statement break",
                    "Use ASCII quotes, and one command per line separated by ;");
            }
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
            return CheckChangeDirectory(segment, bash, previousSeparator, words, ref directory, ref departure, origin);
        }

        return CheckCommand(name, words, tokens.Quoted ?? [], directory, departure, origin, planningDirectories);
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
    private static Refusal? CheckChangeDirectory(
        Segment segment,
        bool bash,
        string? previousSeparator,
        List<string> words,
        ref string directory,
        ref string? departure,
        string? origin)
    {
        if (words.Count != 2)
        {
            return new Refusal("needs exactly one directory operand", "Name one directory: cd <dir>");
        }

        if (IsFlag(words[1]))
        {
            return new Refusal("uses a flag or cd -, which is not allowed", "Name the directory itself: cd <dir>");
        }

        if (words[1].StartsWith('~'))
        {
            return new Refusal(
                "changes into a home directory, which is not allowed",
                "Name a directory beneath the working directory");
        }

        // A cd is honoured only where the call's own chain carries it: first in the call, or right after &&
        // (or ; in PowerShell). After || or | the cd may not run, so the directory it would set is not known.
        bool honoured = previousSeparator is null
            || string.Equals(previousSeparator, "&&", StringComparison.Ordinal)
            || (!bash && string.Equals(previousSeparator, ";", StringComparison.Ordinal));
        if (!honoured)
        {
            return new Refusal(
                "changes directory after a separator that may not run it, which is not allowed",
                "Put the cd first in the call, or right after &&");
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

        // A cd may leave the tree: reads still work from there. The departure is remembered, so a later touch that
        // would anchor outside the tree can name the cd that took it there. Coming back inside clears it.
        departure = origin is not null && !IsWithin(resolved, origin) ? segment.Text : null;
        directory = resolved;
        return null;
    }

    private static Refusal? CheckCommand(
        string name,
        List<string> words,
        List<bool> quoted,
        string directory,
        string? departure,
        string? origin,
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
            case KyberWeaveCli:
                return CheckKyberWeave(words);
            case "codegraph":
                // Exactly one query operand. A quoted query is an operand even when it begins with a dash; an unquoted
                // dash-leading word is a flag, and explore takes none.
                return words.Count == 3
                    && string.Equals(words[1], "explore", StringComparison.Ordinal)
                    && words[2].Length > 0
                    && (!IsFlag(words[2]) || (quoted.Count > 2 && quoted[2]))
                    ? null
                    : new Refusal(
                        "is not a codegraph form on the architect's list",
                        "Use codegraph explore \"<query>\"");
            case "git":
                return CheckGit(words);
            case "grep":
            case "ls":
            case "head":
            case "wc":
            case "cut":
                return null;
            case "rg":
                return CheckRipgrep(words);
            case "tail":
                return CheckTail(words);
            case "find":
                return CheckFind(words);
            case "sort":
                return CheckSort(words);
            case "uniq":
                return CheckUniq(words);
            case "touch":
                return CheckTouch(words, directory, departure, origin, planningDirectories);
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
        // doctor --ensure takes no operand: an extra path would be a second target the form does not name.
        bool doctor = words.Count == 3
            && string.Equals(words[1], "doctor", StringComparison.Ordinal)
            && string.Equals(words[2], "--ensure", StringComparison.Ordinal);

        // The plan reader: exactly one path operand, then -f json. Any other arbiter subcommand (eval, setup,
        // install, uninstall, and the rest) is not this form and is refused by the default branch.
        bool arbiterPlan = words.Count == 6
            && string.Equals(words[1], "arbiter", StringComparison.Ordinal)
            && string.Equals(words[2], "plan", StringComparison.Ordinal)
            && !IsFlag(words[3])
            && string.Equals(words[4], "-f", StringComparison.Ordinal)
            && string.Equals(words[5], "json", StringComparison.Ordinal);

        return validate || drift || doctor || arbiterPlan
            ? null
            : new Refusal(
                "is not a kyber-weave form on the architect's list",
                "Use kyber-weave docs validate <dir> --merge-ready, docs drift <dir>, doctor --ensure, or arbiter plan <plan file> -f json");
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
            else if (string.Equals(words[index], "-C", StringComparison.Ordinal)
                && index + 1 < words.Count
                && !IsFlag(words[index + 1]))
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
            if (IsDeniedGitLongOption(words[argument]))
            {
                return new Refusal(
                    "writes its output to a file, which is not allowed",
                    "Read the output from standard output");
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a git long option is one of the options that writes a file or runs a program. GNU parse-options
    /// accepts any unambiguous abbreviation of an option, so a name is refused when it is a prefix of a denied
    /// option (<c>--o</c>, <c>--ou</c>, <c>--ex</c>, <c>--te</c>) or begins with one (<c>--output=f</c>). An
    /// ordinary option such as <c>--oneline</c> is neither, and stays allowed.
    /// </summary>
    private static bool IsDeniedGitLongOption(string argument)
    {
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        string name = LongOptionName(argument)[2..];
        if (name.Length == 0 || GitBenignOutputOptions.Contains(name))
        {
            return false;
        }

        foreach (string denied in GitDeniedLongOptions)
        {
            if (denied.StartsWith(name, StringComparison.Ordinal) || name.StartsWith(denied, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>
    /// Checks <c>rg</c> by denying the options that run a program: a preprocessor (<c>--pre</c>), a hostname
    /// command (<c>--hostname-bin</c>), a hyperlink format (<c>--hyperlink-format</c>), and compressed input
    /// (<c>--search-zip</c>, <c>-z</c>). The families are matched by prefix, so an abbreviated spelling of one
    /// of them is refused too.
    /// </summary>
    /// <remarks>
    /// A denylist rather than an allowlist: rg has more than a hundred options, and a list that misses one
    /// would refuse an ordinary search. The executing options are few and named, so the denial stays exact.
    /// </remarks>
    private static Refusal? CheckRipgrep(List<string> words)
    {
        foreach (string argument in words.Skip(1))
        {
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (IsDeniedRipgrepLongOption(LongOptionName(argument)))
                {
                    return RipgrepRefusal;
                }

                continue;
            }

            if (HasShortFlag(argument, 'z'))
            {
                return RipgrepRefusal;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an rg long option is one of the options that run a program or read compressed input. Matched in
    /// both directions: a name of two or more characters is refused when it is a prefix of a denied option
    /// (<c>--pr</c>, <c>--pre</c>, <c>--hyper</c>) or begins with one. Options such as <c>--glob</c>, <c>--type</c>
    /// and <c>--max-count</c> match neither way and stay allowed.
    /// </summary>
    private static bool IsDeniedRipgrepLongOption(string option)
    {
        string name = option[2..];
        if (name.Length < 2 || string.Equals(name, "pretty", StringComparison.Ordinal))
        {
            // --pretty is rg's own output format (-p), not a program or a file: it is the one name the prefix rule
            // would otherwise catch, because it begins with --pre.
            return false;
        }

        foreach (string denied in RipgrepExecutingOptions)
        {
            string bare = denied[2..];
            if (bare.StartsWith(name, StringComparison.Ordinal) || name.StartsWith(bare, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly Refusal RipgrepRefusal = new(
        "runs a program or reads compressed input through rg, which is not allowed",
        "Search the plain files with grep or rg without those options");

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

    /// <summary>
    /// Checks <c>sort</c> against an allowlist of flags that only order the output. Every other option is
    /// refused, because sort has options that write files (<c>-o</c>, <c>--output</c>) or run programs
    /// (<c>--compress-program</c>), and GNU sort also accepts unambiguous long-option abbreviations, so a
    /// denylist of spellings cannot be complete. Operands are input files, read only.
    /// </summary>
    private static Refusal? CheckSort(List<string> words)
    {
        for (int index = 1; index < words.Count; index++)
        {
            string argument = words[index];
            if (argument == "--")
            {
                break;
            }

            if (!IsFlag(argument) || argument == "-")
            {
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (!SortSafeLongFlags.Contains(LongOptionName(argument)))
                {
                    return SortRefusal;
                }

                continue;
            }

            for (int letter = 1; letter < argument.Length; letter++)
            {
                // -k and -t take a value: when attached, the rest of the word is that value.
                if (argument[letter] is 'k' or 't')
                {
                    break;
                }

                // The ordering flags -r -n -u -f -b -h -V -s -g -d -i -M -R; any other letter is refused.
                if (argument[letter] is not ('r' or 'n' or 'u' or 'f' or 'b' or 'h' or 'V' or 's' or 'g' or 'd' or 'i' or 'M' or 'R'))
                {
                    return SortRefusal;
                }
            }
        }

        return null;
    }

    private static readonly Refusal SortRefusal = new(
        "uses a sort option outside the ordering set, and some sort options write files or run programs",
        "Use sort with ordering flags only, and read the sorted output from standard output");

    /// <summary>The name of a long option, without any <c>=value</c> part.</summary>
    private static string LongOptionName(string argument)
    {
        int equals = argument.IndexOf('=', StringComparison.Ordinal);
        return equals < 0 ? argument : argument[..equals];
    }

    /// <summary>
    /// Checks <c>tail</c>: a read of a finite file or pipe. Following (<c>-f</c>, <c>-F</c>, <c>--follow</c>)
    /// never returns, and would hang the agent's call, so it is refused. A long option beginning <c>--f</c> is
    /// refused too, since GNU tail accepts an abbreviation of <c>--follow</c>.
    /// </summary>
    private static Refusal? CheckTail(List<string> words)
    {
        foreach (string argument in words.Skip(1))
        {
            if (argument.StartsWith("--f", StringComparison.Ordinal)
                || HasShortFlag(argument, 'f')
                || HasShortFlag(argument, 'F'))
            {
                return new Refusal(
                    "follows a file, which never returns and would hang the call",
                    "Read the end of the file with tail without -f, or use the Read tool with offset and limit");
            }
        }

        return null;
    }

    /// <summary>
    /// Checks <c>uniq</c>: at most one file operand. The second operand of uniq is an output file, so a second
    /// operand is refused. Every word after <c>--</c> is an operand, and <c>-f</c>, <c>-s</c> and <c>-w</c> (and their
    /// long forms) take their value as the next word, which is not an operand.
    /// </summary>
    private static Refusal? CheckUniq(List<string> words)
    {
        int operands = 0;
        bool optionsEnded = false;
        for (int index = 1; index < words.Count; index++)
        {
            string argument = words[index];
            if (!optionsEnded && string.Equals(argument, "--", StringComparison.Ordinal))
            {
                optionsEnded = true;
                continue;
            }

            if (optionsEnded || !IsFlag(argument) || argument == "-")
            {
                operands++;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (string.Equals(argument, LongOptionName(argument), StringComparison.Ordinal)
                    && UniqValueLongOptions.Contains(argument))
                {
                    index++;
                }

                continue;
            }

            for (int letter = 1; letter < argument.Length; letter++)
            {
                if (argument[letter] is 'f' or 's' or 'w')
                {
                    // The value is the rest of this word when attached (-f1), otherwise the next word (-f 1).
                    if (letter == argument.Length - 1)
                    {
                        index++;
                    }

                    break;
                }
            }
        }

        return operands > 1
            ? new Refusal(
                "names two files, and the second would be an output file, which is not allowed",
                "Pass at most one input file to uniq")
            : null;
    }

    /// <summary>
    /// Checks <c>touch</c>: literal operands with no flags, each resolved against the effective directory,
    /// beneath one of the planning directories, and named <c>.md</c> or <c>.json</c>.
    /// </summary>
    private static Refusal? CheckTouch(
        List<string> words,
        string directory,
        string? departure,
        string? origin,
        IReadOnlyList<string> planningDirectories)
    {
        if (words.Count < 2)
        {
            return new Refusal(
                "names no file to create",
                "Name one literal file path beneath the plan, specification or todo directory");
        }

        // Fail closed: without a usable payload directory there is no tree to anchor a path to.
        if (origin is null)
        {
            return new Refusal(
                "has no usable working directory to anchor the path to",
                "Run the call from the repository's working directory");
        }

        // A cd may move the effective directory only within the payload's own tree. Once it has left, touch has no
        // tree to check a path against, and a relative name would land wherever the cd took it.
        if (!IsWithin(directory, origin))
        {
            return new Refusal(
                "changes into a directory outside the working tree, and touch cannot anchor a path there",
                "Create the file from the working directory, without changing into another tree first",
                departure);
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

            if (operand.StartsWith('~') || !IsBeneathPlanningDirectory(path, origin, planningDirectories))
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

    /// <summary>
    /// Whether <paramref name="directory"/> is <paramref name="root"/> or lies beneath it.
    /// </summary>
    private static bool IsWithin(string directory, string root)
    {
        string prefix = root.EndsWith('/') ? root : root + "/";
        return string.Equals(directory, root, StringComparison.Ordinal)
            || directory.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a resolved path lies under one of the planning directories of the payload's own tree
    /// (<paramref name="origin"/>): the path must start with the origin, and the rest must start with a planning
    /// directory. Each planning directory is normalised first, so <c>docs/./plans</c>, a trailing slash and a
    /// backslash name the same directory as <c>docs/plans</c>. A directory that climbs out of the tree names
    /// nothing and never matches.
    /// </summary>
    private static bool IsBeneathPlanningDirectory(
        string path,
        string origin,
        IReadOnlyList<string> planningDirectories)
    {
        string root = origin.EndsWith('/') ? origin : origin + "/";
        if (!path.StartsWith(root, StringComparison.Ordinal))
        {
            return false;
        }

        string relative = path[root.Length..];
        foreach (string planning in planningDirectories)
        {
            string normalized = NormalizeRelativeDirectory(planning);
            if (normalized.Length > 0 && relative.StartsWith(normalized + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalises a relative planning directory to one forward-slash spelling with no leading, trailing or doubled
    /// separators and no <c>.</c> segments. Returns the empty string when the directory is empty or climbs with
    /// <c>..</c>, so it matches nothing.
    /// </summary>
    private static string NormalizeRelativeDirectory(string directory)
    {
        List<string> parts = [];
        foreach (string part in directory.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                return string.Empty;
            }

            parts.Add(part);
        }

        return string.Join("/", parts);
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
        if (IsRooted(forward))
        {
            return Normalize(forward);
        }

        // A relative operand needs a rooted base. A blank or unrooted base would otherwise join to "/<operand>",
        // anchoring the path to the file-system root, so it resolves to nothing instead.
        string baseForward = baseDirectory.Replace('\\', '/');
        return IsRooted(baseForward) ? Normalize($"{baseForward.TrimEnd('/')}/{forward}") : null;
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
    /// <summary>
    /// The words of one segment, the refusal that stopped the read if any, and for each word whether any part of it
    /// was quoted. The quoting matters where a dash-leading word is an operand rather than a flag: a quoted
    /// <c>"-x"</c> is a query, and only an unquoted <c>-x</c> is an option.
    /// </summary>
    private sealed record Tokens(List<string> Words, Refusal? Refusal, List<bool>? Quoted = null);

    /// <summary>
    /// Why a construct is refused, and the replacement the architect should use instead. <paramref name="Segment"/>,
    /// when set, is the segment the denial names in place of the one that was refused: a touch refused because an
    /// earlier cd left the tree names that cd.
    /// </summary>
    private sealed record Refusal(string Why, string Replacement, string? Segment = null);
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
