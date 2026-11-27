using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The worker guard's secret rule, <c>KW-ARB-SECRET-001</c>: the pure decision about one subagent's tool call, made
/// from the call's own text. It refuses a call that would read a secret file, that would print a secret from a store,
/// or that would carry a literal secret on the command line, because everything a tool call prints enters the
/// transcript, and the transcript goes to the model provider.
/// </summary>
/// <remarks>
/// The whole command text is matched, quotes and nested shell strings included, so wrapping a command in
/// <c>bash -c</c>, <c>sh -c</c> or <c>pwsh -Command</c> escapes nothing. Two readings are precise rather than
/// word-based: a grep or rg call has its options parsed, so an option's value is never taken for the pattern or a
/// file, and the heredoc body of a known interpreter that reads its script from standard input is judged as a script
/// (the shell text in its string literals, its <c>$(...)</c> and backtick spans, the environment it prints) rather
/// than line by line as shell steps. Every <c>open(...)</c> or <c>Path(...)</c> call anywhere in the command fails
/// closed on each literal or assigned value it could open. A command whose text cannot be read is refused rather
/// than allowed, which is why <see cref="Refusal" /> catches its own failure. Listing a directory or
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

    /// <summary>The search programs whose options are parsed, so an option's value is never taken for the pattern or a file.</summary>
    private static readonly HashSet<string> OptionParsedSearchPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "grep", "egrep", "fgrep", "rg",
    };

    /// <summary>The short grep, egrep and fgrep options that take a value.</summary>
    private static readonly SearchValues<char> GrepValueLetters = SearchValues.Create("efABCmdD");

    /// <summary>
    /// The short interpreter flags that take no value and name no script, so an interpreter given only these and
    /// <c>-</c> still reads its script from a heredoc. Case matters: Python's <c>-u</c>, <c>-B</c>, <c>-I</c>,
    /// <c>-s</c>, <c>-S</c>, <c>-q</c> and <c>-O</c>, and Perl's or Ruby's <c>-w</c>.
    /// </summary>
    private const string StandardInputFlagLetters = "uBIsSqOw";

    /// <summary>The short rg options that take a value.</summary>
    private static readonly SearchValues<char> RipgrepValueLetters = SearchValues.Create("efABCmdgtTMj");

    /// <summary>The short grep and rg options that supply the pattern, so every free word is a file.</summary>
    private static readonly SearchValues<char> SearchPatternLetters = SearchValues.Create("ef");

    /// <summary>The short grep and rg options whose value names a file or a glob of files, so the value is judged.</summary>
    private const string SearchJudgedLetters = "fg";

    /// <summary>The long grep and rg options that take a value, as <c>--name=value</c> or as the next word.</summary>
    private static readonly HashSet<string> SearchValueLongOptions = new(StringComparer.Ordinal)
    {
        "--regexp", "--file", "--after-context", "--before-context", "--context", "--max-count", "--glob", "--iglob",
        "--include", "--exclude", "--type", "--type-not", "--exclude-from", "--include-from", "--ignore-file",
    };

    /// <summary>The long grep and rg options whose value names a file or a glob of files, so the value is judged.</summary>
    private static readonly HashSet<string> SearchJudgedLongOptions = new(StringComparer.Ordinal)
    {
        "--file", "--exclude-from", "--include-from", "--ignore-file", "--glob", "--iglob", "--include", "--exclude",
    };

    /// <summary>The long grep and rg options that supply the pattern, so every free word is a file.</summary>
    private static readonly HashSet<string> SearchPatternLongOptions = new(StringComparer.Ordinal)
    {
        "--regexp", "--file",
    };

    /// <summary>The characters a word is trimmed of before it is read as a program name, as <see cref="Words" /> trims.</summary>
    private static readonly char[] WordEdges = ['(', ')', '{', '}', '`', ',', ';', '&', '|', '<', '>', '!'];

    /// <summary>The characters that separate the words of a script expression once its literals are taken out.</summary>
    private static readonly char[] ScriptWordSeparators =
        [' ', '\t', '\r', '\n', ',', '+', '(', ')', '[', ']', '{', '}', '=', '\\', '*', '%'];

    /// <summary>How deep quoted nested shell text is read again, so a pathological command cannot recurse forever.</summary>
    private const int MaximumNesting = 8;

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
        "stop and return " + ReturnStatus.Blocked + " with an Open item: line saying so.";

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

        HeredocSplit split = SplitInterpreterHeredocs(command);
        foreach (string step in Steps(split.Shell))
        {
            if (StepRefusal(step) is { } refusal)
            {
                return refusal;
            }
        }

        foreach (ScriptBody body in split.Bodies)
        {
            if (ScriptBodyRefusal(body) is { } refusal)
            {
                return refusal;
            }
        }

        return ScriptOpensSecretFile(command) ? RefusalText(SecretCase.ReadsSecretFile) : null;
    }

    /// <summary>The refusal one shell step earns, or null.</summary>
    private static string? StepRefusal(string step)
    {
        if (ReadsSecretFile(step) || SearchReadsSecretFile(step))
        {
            return RefusalText(SecretCase.ReadsSecretFile);
        }

        if (PrintsStoredSecret(step))
        {
            return RefusalText(SecretCase.PrintsStoredSecret);
        }

        return PutsLiteralSecret(step) ? RefusalText(SecretCase.PutsLiteralSecret) : null;
    }

    /// <summary>
    /// The refusal a known interpreter's heredoc body earns, or null. The body is a script, so its lines are not
    /// read as shell readers; what the script could still expose is judged instead: shell text inside its string
    /// literals (a <c>subprocess</c> or <c>os.system</c> call), read both line by line and across lines so a
    /// triple-quoted or multi-line literal is judged too, the whole environment, and on each line the printing and
    /// literal-secret checks a shell step gets. Every <c>$(...)</c> or backtick span in the body is judged as a shell
    /// step whatever the delimiter: the shell expands an unquoted body first, and Ruby, Perl and a Node template
    /// literal can run a backtick span as shell themselves.
    /// </summary>
    private static string? ScriptBodyRefusal(ScriptBody body)
    {
        IEnumerable<string> literals = QuotedLiteral().Matches(body.Text).Select(LiteralText)
            .Concat(MultiLineLiteral().Matches(body.Text).Select(match => match.Groups["text"].Value));
        if (Steps(string.Join(' ', literals))
            .Any(literal => ReadsSecretFile(literal) || SearchReadsSecretFile(literal)))
        {
            return RefusalText(SecretCase.ReadsSecretFile);
        }

        if (WholeEnvironment().IsMatch(body.Text))
        {
            return RefusalText(SecretCase.PrintsStoredSecret);
        }

        foreach (string step in ExpandedCommands(body.Text).SelectMany(Steps))
        {
            if (StepRefusal(step) is { } refusal)
            {
                return refusal;
            }
        }

        foreach (string line in body.Text.Split('\n'))
        {
            if (PrintsStoredSecret(line))
            {
                return RefusalText(SecretCase.PrintsStoredSecret);
            }

            if (PutsLiteralSecret(line))
            {
                return RefusalText(SecretCase.PutsLiteralSecret);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether this step reads a secret file: a reading program takes one as an argument, or a single <c>&lt;</c>
    /// redirect feeds one to the step. A grep or rg call is judged by <see cref="SearchReadsSecretFile" />, which
    /// parses its options; the word scan here reads it too only when that parser found fewer search calls than the
    /// step's words name, so a call the parser missed still fails closed. Listing a directory and testing a file
    /// names no reader, so neither is judged here.
    /// </summary>
    private static bool ReadsSecretFile(string step)
    {
        string[] words = Words(step);
        if (!words.Any(IsSecretFile))
        {
            return false;
        }

        bool scanSearches = SearchCallsMissed(step, words);
        for (int index = 0; index < words.Length; index++)
        {
            if (!scanSearches && OptionParsedSearchPrograms.Contains(ProgramName(words[index])))
            {
                continue;
            }

            if (ReadsSecretFileArgument(words, index))
            {
                return true;
            }
        }

        return ReadsThroughInputRedirect(step);
    }

    /// <summary>
    /// Whether the option parser found fewer grep or rg calls in this step than its words name, so the word scan
    /// must read them as well.
    /// </summary>
    private static bool SearchCallsMissed(string step, string[] words)
    {
        int named = words.Count(word => OptionParsedSearchPrograms.Contains(ProgramName(word)));
        if (named == 0)
        {
            return false;
        }

        List<SearchCall> calls = [];
        FindSearchCalls(step, calls, 0);
        return calls.Count < named;
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

    /// <summary>
    /// Whether a grep or rg call in this step reads a secret file, read from the step's raw text so a quoted
    /// pattern is one word. Every call is found, a call inside a quoted nested shell string too.
    /// </summary>
    private static bool SearchReadsSecretFile(string step)
    {
        List<SearchCall> calls = [];
        FindSearchCalls(step, calls, 0);
        return calls.Any(SearchArgumentsReadSecretFile);
    }

    /// <summary>
    /// The arguments of every grep or rg call in this text: a word that names one starts a call whose arguments are
    /// the words after it, and a quoted word holding a space is read again as the shell text a nested shell runs.
    /// </summary>
    private static void FindSearchCalls(string text, List<SearchCall> calls, int depth)
    {
        if (depth > MaximumNesting)
        {
            return;
        }

        List<ShellWord> words = ShellWords(text);
        for (int index = 0; index < words.Count; index++)
        {
            if (words[index].Quoted && words[index].Text.Any(char.IsWhiteSpace))
            {
                foreach (string nested in Steps(words[index].Text))
                {
                    FindSearchCalls(nested, calls, depth + 1);
                }
            }

            string program = ProgramName(words[index].Text.Trim(WordEdges));
            if (OptionParsedSearchPrograms.Contains(program))
            {
                calls.Add(new SearchCall(program, [.. words.Skip(index + 1).Select(word => word.Text)]));
            }
        }
    }

    /// <summary>
    /// Whether grep or rg, given these arguments, reads a secret file. Options are parsed as the programs parse
    /// them: a short bundle letter by letter, where the last letter that takes a value takes the rest of the bundle
    /// or else the next word, and a long option its <c>=</c> value or the next word. A value that names a file or a
    /// glob of files is judged wherever it stands. The first free word is the pattern unless <c>-e</c>,
    /// <c>-f</c> or their long forms supplied one, and every other free word is a file. Which short options take a
    /// value is the program's own: <c>-g</c>, <c>-t</c>, <c>-T</c>, <c>-M</c> and <c>-j</c> do in rg and are flags in
    /// grep. An option this rule does not know is read as a flag, as before.
    /// </summary>
    private static bool SearchArgumentsReadSecretFile(SearchCall call)
    {
        string[] arguments = call.Arguments;
        SearchValues<char> valueLetters = string.Equals(call.Program, "rg", StringComparison.OrdinalIgnoreCase)
            ? RipgrepValueLetters
            : GrepValueLetters;
        bool patternSupplied = false;
        bool optionsEnded = false;
        List<string> free = [];
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (optionsEnded || argument.Length < 2 || argument[0] != '-')
            {
                free.Add(argument);
                continue;
            }

            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                optionsEnded = true;
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                int equals = argument.IndexOf('=', StringComparison.Ordinal);
                string name = equals < 0 ? argument : argument[..equals];
                string? value = equals < 0 ? null : argument[(equals + 1)..];
                if (value is null && SearchValueLongOptions.Contains(name) && index + 1 < arguments.Length)
                {
                    value = arguments[++index];
                }

                patternSupplied |= SearchPatternLongOptions.Contains(name);
                if (value is not null && SearchJudgedLongOptions.Contains(name) && IsSecretFile(value))
                {
                    return true;
                }

                continue;
            }

            string bundle = argument[1..];
            int lastValueLetter = bundle.AsSpan().LastIndexOfAny(valueLetters);
            string? next = lastValueLetter == bundle.Length - 1 && index + 1 < arguments.Length
                ? arguments[++index]
                : null;
            patternSupplied |= bundle.AsSpan().ContainsAny(SearchPatternLetters);
            foreach (char letter in SearchJudgedLetters)
            {
                int position = bundle.IndexOf(letter, StringComparison.Ordinal);
                if (position < 0)
                {
                    continue;
                }

                string attached = bundle[(position + 1)..];
                if ((attached.Length > 0 && IsSecretFile(attached)) || (next is not null && IsSecretFile(next)))
                {
                    return true;
                }
            }
        }

        return free.Skip(patternSupplied ? 0 : 1).Any(IsSecretFile);
    }

    /// <summary>
    /// Whether an <c>open(...)</c> or <c>Path(...)</c> call anywhere in the command could open a secret file. It
    /// fails closed: every quoted literal inside the call is judged, every value assigned anywhere in the command to
    /// an identifier the call names is judged, and every other unquoted word in it is judged as a path. Only a first
    /// argument that is one bare identifier never assigned in the command is not a path, because then the match was
    /// the variable's own name.
    /// </summary>
    private static bool ScriptOpensSecretFile(string command)
    {
        foreach (Match call in FileOpeningCall().Matches(command))
        {
            int open = call.Index + call.Length - 1;
            int close = MatchingParenthesis(command, open);
            string argument = close < 0 ? command[(open + 1)..] : command[(open + 1)..close];
            if (OpenedArgumentIsSecret(argument, command))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the argument text of one <c>open(...)</c> or <c>Path(...)</c> call could name a secret file.</summary>
    private static bool OpenedArgumentIsSecret(string argument, string command)
    {
        if (QuotedLiteral().Matches(argument).Select(LiteralText).Any(IsSecretFile))
        {
            return true;
        }

        string first = FirstArgument(argument).Trim();
        bool loneIdentifier = Identifier().IsMatch(first);
        foreach (string word in UnquotedWords(argument))
        {
            if (!Identifier().IsMatch(word))
            {
                if (IsSecretFile(word))
                {
                    return true;
                }

                continue;
            }

            if (AssignedValueIsSecret(word, command, new HashSet<string>(StringComparer.Ordinal)))
            {
                return true;
            }

            bool ownNameOnly = loneIdentifier && string.Equals(word, first, StringComparison.Ordinal);
            if (!ownNameOnly && IsSecretFile(word))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether any value assigned to this identifier anywhere in the command could name a secret file: every
    /// quoted literal in the assigned value, every other unquoted word in it, and the values of the identifiers it
    /// names in turn.
    /// </summary>
    private static bool AssignedValueIsSecret(string identifier, string command, HashSet<string> visited)
    {
        if (!visited.Add(identifier))
        {
            return false;
        }

        foreach (Match assignment in IdentifierAssignment().Matches(command))
        {
            if (!string.Equals(assignment.Groups["name"].Value, identifier, StringComparison.Ordinal))
            {
                continue;
            }

            string value = assignment.Groups["value"].Value;
            if (QuotedLiteral().Matches(value).Select(LiteralText).Any(IsSecretFile))
            {
                return true;
            }

            foreach (string word in UnquotedWords(value))
            {
                bool secret = Identifier().IsMatch(word)
                    ? AssignedValueIsSecret(word, command, visited)
                    : IsSecretFile(word);
                if (secret)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The text of a call's first argument: everything before its first comma outside quotes and brackets.</summary>
    private static string FirstArgument(string argument)
    {
        int depth = 0;
        char quote = '\0';
        for (int index = 0; index < argument.Length; index++)
        {
            char current = argument[index];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (current)
            {
                case '"' or '\'':
                    quote = current;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    return argument[..index];
            }
        }

        return argument;
    }

    /// <summary>The words of a script's text once its quoted literals are taken out, split at its operators.</summary>
    private static string[] UnquotedWords(string text) =>
        QuotedLiteral().Replace(text, " ")
            .Split(ScriptWordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('"', '\'', '`'))
            .Where(word => word.Length > 0)
            .ToArray();

    /// <summary>The text inside one quoted literal match.</summary>
    private static string LiteralText(Match literal) =>
        literal.Groups["single"].Value + literal.Groups["double"].Value;

    /// <summary>
    /// The command with each known script interpreter's heredoc body taken out of its shell text, and those bodies
    /// apart. A heredoc counts only when its <c>&lt;&lt;</c> stands outside quotes, its delimiter is a plain word,
    /// its terminator line is found, and the step that opens it runs <c>python</c>, <c>py</c>, <c>node</c>,
    /// <c>ruby</c> or <c>perl</c> itself. Any other heredoc keeps its body lines in the shell text, as before.
    /// </summary>
    private static HeredocSplit SplitInterpreterHeredocs(string command)
    {
        StringBuilder shell = new();
        List<ScriptBody> bodies = [];
        List<PendingHeredoc> pending = [];
        char quote = '\0';
        int stepStart = 0;
        for (int index = 0; index < command.Length; index++)
        {
            char current = command[index];
            if (quote != '\0')
            {
                shell.Append(current);
                if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                shell.Append(current);
                continue;
            }

            if (current == '<' && IsHeredocOperator(command, index)
                && HeredocDelimiter(command, index + 2) is { } heredoc)
            {
                bool interpreter = IsInterpreterStep(command[stepStart..index]);
                pending.Add(new PendingHeredoc(heredoc.Delimiter, heredoc.StripsTabs, interpreter));
                shell.Append(command[index..heredoc.End]);
                index = heredoc.End - 1;
                continue;
            }

            shell.Append(current);
            if (current is ';' or '&' or '|')
            {
                stepStart = index + 1;
            }

            if (current != '\n')
            {
                continue;
            }

            stepStart = index + 1;
            int position = index + 1;
            foreach (PendingHeredoc opened in pending)
            {
                if (HeredocBody(command, position, opened) is not { } body)
                {
                    shell.Append(command[position..]);
                    position = command.Length;
                    break;
                }

                if (opened.Interpreter)
                {
                    bodies.Add(new ScriptBody(command[position..body.BodyEnd]));
                }
                else
                {
                    shell.Append(command[position..body.After]);
                }

                position = body.After;
            }

            pending.Clear();
            index = position - 1;
            stepStart = position;
        }

        return new HeredocSplit(shell.ToString(), bodies);
    }

    /// <summary>Whether the <c>&lt;</c> at this index starts a <c>&lt;&lt;</c> heredoc rather than a here-string.</summary>
    private static bool IsHeredocOperator(string command, int index) =>
        index + 1 < command.Length
        && command[index + 1] == '<'
        && (index + 2 >= command.Length || command[index + 2] != '<')
        && (index == 0 || command[index - 1] != '<');

    /// <summary>
    /// The heredoc delimiter that starts at this index, after the <c>&lt;&lt;</c>, or null when it is not a plain
    /// word, so an arithmetic shift or an odd form is never read as a heredoc.
    /// </summary>
    private static HeredocOpening? HeredocDelimiter(string command, int start)
    {
        int position = start;
        bool stripsTabs = position < command.Length && command[position] == '-';
        if (stripsTabs)
        {
            position++;
        }

        while (position < command.Length && command[position] is ' ' or '\t')
        {
            position++;
        }

        StringBuilder delimiter = new();
        while (position < command.Length)
        {
            char current = command[position];
            if (current is '"' or '\'')
            {
                int close = command.IndexOf(current, position + 1);
                if (close < 0)
                {
                    return null;
                }

                delimiter.Append(command, position + 1, close - position - 1);
                position = close + 1;
                continue;
            }

            if (current == '\\')
            {
                position++;
                continue;
            }

            if (char.IsWhiteSpace(current) || current is ';' or '&' or '|' or '<' or '>' or '(' or ')')
            {
                break;
            }

            delimiter.Append(current);
            position++;
        }

        string word = delimiter.ToString();
        return Identifier().IsMatch(word) ? new HeredocOpening(word, stripsTabs, position) : null;
    }

    /// <summary>The body of a heredoc that starts at this index, or null when its terminator line never comes.</summary>
    private static HeredocExtent? HeredocBody(string command, int start, PendingHeredoc heredoc)
    {
        int lineStart = start;
        while (lineStart <= command.Length)
        {
            int newline = command.IndexOf('\n', lineStart);
            int lineEnd = newline < 0 ? command.Length : newline;
            string line = command[lineStart..lineEnd].TrimEnd('\r');
            if (heredoc.StripsTabs)
            {
                line = line.TrimStart('\t');
            }

            if (string.Equals(line, heredoc.Delimiter, StringComparison.Ordinal))
            {
                return new HeredocExtent(lineStart, newline < 0 ? command.Length : newline + 1);
            }

            if (newline < 0)
            {
                return null;
            }

            lineStart = newline + 1;
        }

        return null;
    }

    /// <summary>
    /// Whether the step that opens a heredoc runs a known script interpreter itself, past any wrapper or
    /// <c>NAME=value</c> word before it, that reads its script from standard input: nothing follows the interpreter
    /// but <c>-</c> and plain flags. An interpreter given <c>-c</c>, <c>-e</c>, <c>-m</c>, a script file or any other
    /// word has a script of its own, so the heredoc is data it may hand to a shell, and its lines stay shell steps.
    /// </summary>
    private static bool IsInterpreterStep(string step)
    {
        string[] words =
        [
            .. step.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim(WordEdges))
                .Where(word => word.Length > 0),
        ];
        for (int index = 0; index < words.Length; index++)
        {
            string word = words[index];
            if (CommandWrappers.Contains(word) || EnvironmentAssignmentWord().IsMatch(word))
            {
                continue;
            }

            return InterpreterProgram().IsMatch(ProgramName(word))
                && words[(index + 1)..].All(IsStandardInputScriptFlag);
        }

        return false;
    }

    /// <summary>
    /// Whether a word after an interpreter leaves it reading its script from standard input: <c>-</c> itself, or a
    /// bundle of short flags that take no value and name no script.
    /// </summary>
    private static bool IsStandardInputScriptFlag(string word) =>
        string.Equals(word, "-", StringComparison.Ordinal)
        || (word.Length > 1 && word[0] == '-' && word[1..].All(letter => StandardInputFlagLetters.Contains(letter, StringComparison.Ordinal)));

    /// <summary>
    /// The commands a heredoc body could run as shell: each <c>$(...)</c> and backtick span, which the shell runs
    /// while it expands an unquoted body and Ruby, Perl or a Node template literal can run itself. Quotes do not
    /// protect text in a heredoc body, so none is skipped; a span that never closes runs to the end.
    /// </summary>
    private static List<string> ExpandedCommands(string body)
    {
        List<string> commands = [];
        for (int index = 0; index < body.Length; index++)
        {
            if (body[index] == '$' && index + 1 < body.Length && body[index + 1] == '(')
            {
                int close = MatchingParenthesis(body, index + 1);
                commands.Add(close < 0 ? body[(index + 2)..] : body[(index + 2)..close]);
                index = close < 0 ? body.Length : close;
                continue;
            }

            if (body[index] == '`')
            {
                int close = body.IndexOf('`', index + 1);
                commands.Add(close < 0 ? body[(index + 1)..] : body[(index + 1)..close]);
                index = close < 0 ? body.Length : close;
            }
        }

        return commands;
    }

    /// <summary>
    /// The step's words as a shell reads them: split at whitespace outside quotes, with the quote characters taken
    /// out, and marked when any part of the word was quoted.
    /// </summary>
    private static List<ShellWord> ShellWords(string text)
    {
        List<ShellWord> words = [];
        StringBuilder word = new();
        bool started = false;
        bool quoted = false;
        char quote = '\0';
        foreach (char current in text)
        {
            if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(current);
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                quoted = true;
                started = true;
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                if (started)
                {
                    words.Add(new ShellWord(word.ToString(), quoted));
                    word.Clear();
                    started = false;
                    quoted = false;
                }

                continue;
            }

            word.Append(current);
            started = true;
        }

        if (started)
        {
            words.Add(new ShellWord(word.ToString(), quoted));
        }

        return words;
    }

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
    /// The command split into the steps a shell would run: a separator outside quotes ends one, a separator inside
    /// a quote belongs to the text a nested shell, a query or a value carries, and the command inside a
    /// <c>$(...)</c> or backtick substitution is a step of its own, because the shell runs it as one.
    /// </summary>
    private static IEnumerable<string> Steps(string command)
    {
        foreach (string step in SplitSteps(command))
        {
            yield return step;
            foreach (string substituted in SubstitutedCommands(step))
            {
                yield return substituted;
            }
        }
    }

    /// <summary>The text split into steps at each separator outside quotes, before substitutions are unpacked.</summary>
    private static List<string> SplitSteps(string command)
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
        return steps.Where(candidate => candidate.Trim().Length > 0).ToList();
    }

    /// <summary>
    /// The commands this step runs inside a <c>$(...)</c> or a backtick substitution, each read as a step of its
    /// own. A substitution runs inside double quotes too, so only single-quoted text is skipped as literal.
    /// </summary>
    private static IEnumerable<string> SubstitutedCommands(string step)
    {
        List<string> commands = [];
        char quote = '\0';
        for (int index = 0; index < step.Length; index++)
        {
            char current = step[index];
            if (quote == '\'')
            {
                if (current == '\'')
                {
                    quote = '\0';
                }

                continue;
            }

            if (quote == '"' && current == '"')
            {
                quote = '\0';
                continue;
            }

            if (quote == '\0' && current is '"' or '\'')
            {
                quote = current;
                continue;
            }

            if (current == '$' && index + 1 < step.Length && step[index + 1] == '(')
            {
                int close = MatchingParenthesis(step, index + 1);
                if (close < 0)
                {
                    break;
                }

                commands.Add(step[(index + 2)..close]);
                index = close;
                continue;
            }

            if (current == '`')
            {
                int close = step.IndexOf('`', index + 1);
                if (close < 0)
                {
                    break;
                }

                commands.Add(step[(index + 1)..close]);
                index = close;
            }
        }

        return commands.SelectMany(Steps);
    }

    /// <summary>The index of the <c>)</c> closing the <c>(</c> at this index, or -1 when it never closes.</summary>
    private static int MatchingParenthesis(string text, int open)
    {
        int depth = 0;
        char quote = '\0';
        for (int index = open; index < text.Length; index++)
        {
            char current = text[index];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
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

    /// <summary>One word of a step as a shell reads it, and whether any part of it was quoted.</summary>
    private readonly record struct ShellWord(string Text, bool Quoted);

    /// <summary>A known interpreter's heredoc body, the script that interpreter reads from its standard input.</summary>
    private sealed record ScriptBody(string Text);

    /// <summary>The command's shell text with interpreter heredoc bodies taken out, and those bodies.</summary>
    private sealed record HeredocSplit(string Shell, IReadOnlyList<ScriptBody> Bodies);

    /// <summary>A heredoc opened on the current line whose body starts on the next.</summary>
    private sealed record PendingHeredoc(string Delimiter, bool StripsTabs, bool Interpreter);

    /// <summary>A heredoc's delimiter as written after <c>&lt;&lt;</c>, and the index just past it.</summary>
    private sealed record HeredocOpening(string Delimiter, bool StripsTabs, int End);

    /// <summary>One grep, egrep, fgrep or rg call: the program it runs and the words after it.</summary>
    private sealed record SearchCall(string Program, string[] Arguments);

    /// <summary>Where a heredoc's body ends and where the text after its terminator line starts.</summary>
    private readonly record struct HeredocExtent(int BodyEnd, int After);

    /// <summary>A single- or double-quoted literal on one line.</summary>
    [GeneratedRegex(@"'(?<single>[^'\r\n]*)'|""(?<double>[^""\r\n]*)""")]
    private static partial Regex QuotedLiteral();

    /// <summary>
    /// A string literal read across lines: a triple-quoted one first, else a single- or double-quoted one that may
    /// span lines, so the shell text a script keeps in a multi-line literal is judged too.
    /// </summary>
    [GeneratedRegex(@"'''(?<text>[\s\S]*?)'''|""""""(?<text>[\s\S]*?)""""""|'(?<text>[^']*)'|""(?<text>[^""]*)""")]
    private static partial Regex MultiLineLiteral();

    /// <summary>A script call that opens a file by the path it is given: <c>open(</c> or <c>Path(</c>.</summary>
    [GeneratedRegex(@"(?i)\b(?:open|path)\s*\(")]
    private static partial Regex FileOpeningCall();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    /// <summary>An assignment to a name, in a script or a shell, with the value up to the end of its statement.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\+?=(?!=)(?<value>(?:'[^'\r\n]*'|""[^""\r\n]*""|[^'""\r\n;])*)")]
    private static partial Regex IdentifierAssignment();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*=")]
    private static partial Regex EnvironmentAssignmentWord();

    /// <summary>The script interpreters whose heredoc body is a script rather than shell lines.</summary>
    [GeneratedRegex(@"(?i)^(?:python(?:[0-9]+(?:\.[0-9]+)*)?|py|node|ruby|perl)(?:\.exe)?$")]
    private static partial Regex InterpreterProgram();

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

    [GeneratedRegex(@"(?i)\b(?:echo|printf|write-output|write-host)\b")]
    private static partial Regex PrintingWord();

    /// <summary>A variable reference whose name says it holds a secret, whichever part of the name says so.</summary>
    [GeneratedRegex(@"(?i)\$\{?(?:env:)?[A-Za-z0-9_]*(?:PASSWORD|PASSWD|SECRET|TOKEN|APIKEY|API_KEY|CREDENTIAL)[A-Za-z0-9_]*\}?")]
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
