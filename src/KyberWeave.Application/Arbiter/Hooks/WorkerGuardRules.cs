using System.Globalization;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>The worker guard's pure half: reading a tool call from a harness event, the budget window, and the refusal documents.</summary>
internal static class WorkerGuardRules
{
    /// <summary>The rule id a stray write is refused under.</summary>
    internal const string CheckoutRuleId = "KW-ARB-CHECKOUT-001";

    /// <summary>The rule id an exhausted budget is refused under.</summary>
    internal const string BudgetRuleId = "KW-ARB-BUDGET-001";

    /// <summary>The marker a call line carries when the call started a process: the only kind of call that can hang.</summary>
    internal const string ProcessCallMarker = "p";

    /// <summary>
    /// The marker a call line carries when the call started no process. An older file's bare timestamp means a process
    /// call, so a call that starts none must say so for the per-call limit to tell the two apart.
    /// </summary>
    internal const string NoProcessCallMarker = "n";

    /// <summary>The trigger a sub-agent's own command call raises, so a technology's rules can allow-list its forms.</summary>
    internal const string CommandTrigger = "worker.command";

    /// <summary>The fact holding the command as the harness reported it.</summary>
    internal const string CommandTextFact = "command.text";

    /// <summary>The fact holding the command's form: its program and leading words, options and their values removed.</summary>
    internal const string CommandFormFact = "command.form";

    /// <summary>The fact that is true when the command is one command, with no join, redirect, subshell or substitution.</summary>
    internal const string CommandSingleFact = "command.single";

    /// <summary>The fact holding the form's words before its first option, where a mutating verb appears.</summary>
    internal const string CommandHeadFact = "command.head";

    /// <summary>The fact holding every word of the command, so a rule sees the tool however it was reached.</summary>
    internal const string CommandWordsFact = "command.words";

    /// <summary>The fact holding the command's program, the form's first word.</summary>
    internal const string CommandProgramFact = "command.program";

    /// <summary>The fact naming the calling sub-agent.</summary>
    internal const string WorkerNameFact = "worker.name";

    /// <summary>The fact naming the calling sub-agent's capability profile.</summary>
    internal const string WorkerProfileFact = "worker.capability-profile";

    /// <summary>The fact naming the harness the call arrived from.</summary>
    internal const string WorkerHarnessFact = "worker.harness";

    /// <summary>The capability profile whose command calls a technology's allow-list judges: read-only, can run commands.</summary>
    internal const string PlatformReaderProfile = "platform-reader";

    /// <summary>The status line a worker whose budget ran out returns.</summary>
    internal const string BudgetExceededStatus = "STATUS: BUDGET_EXCEEDED";

    /// <summary>The Copilot CLI harness token.</summary>
    internal const string CopilotCliHarness = "copilot-cli";

    /// <summary>The Claude Code harness token.</summary>
    internal const string ClaudeHarness = "claude";

    /// <summary>The closing line a seat stopped by its budget writes, naming what it did not examine.</summary>
    internal const string SeatBudgetLine = "Budget reached, not examined:";

    /// <summary>The capability profile of a review council seat, which carries the shorter seat budget.</summary>
    internal const string SeatProfile = "review-seat";

    /// <summary>How long a budget file is kept after its last write.</summary>
    internal static readonly TimeSpan BudgetRetention = TimeSpan.FromDays(2);

    /// <summary>The capability profiles whose dispatches carry a budget: the developers and docs-dev.</summary>
    private static readonly HashSet<string> BudgetedProfiles = new(StringComparer.Ordinal) { "worker", "documentation" };

    /// <summary>The leading words a command may carry before its program, which the form skips.</summary>
    private static readonly HashSet<string> CommandWrappers = new(StringComparer.Ordinal)
    {
        "sudo", "env", "command", "exec", "nohup",
    };

    /// <summary>The characters that join, redirect, group or negate commands: a command holding one has no single form.</summary>
    private static readonly System.Buffers.SearchValues<char> ShellControl =
        System.Buffers.SearchValues.Create(";&|<>`(){}!~\n\r");

    /// <summary>The punctuation a word may still carry from the shell around it, removed before a rule reads it.</summary>
    private static readonly char[] WordWrapping = ['\'', '"', '(', ')', '{', '}', '!', '~', '`', ';', '&', '|', '<', '>'];

    /// <summary>
    /// Whether the event can need a refusal at all, decided from the event alone: a root agent, an agent outside the Squad
    /// catalog, and a developer-less call that neither writes a file nor runs a shell command are never guarded. The guard
    /// process asks it before it builds anything, because the guard runs on every tool call.
    /// </summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="stdin">The hook event as JSON.</param>
    /// <returns>False only when the guard would allow without reading a file; true when it must decide, and when the event is unreadable, so the guard reports it.</returns>
    internal static bool NeedsDecision(string harness, string stdin)
    {
        try
        {
            if (WorkerGuardEventReader.TurnEndOf(harness, stdin) is not null)
            {
                return true;
            }

            ToolCall? call = WorkerGuardEventReader.Read(harness, stdin);
            return call is not null
                && (call.WritePath is not null
                    || call.ReadPath is not null
                    || call.Command is not null
                    || IsCommandJudged(call)
                    || string.Equals(call.Agent.CapabilityProfile, SeatProfile, StringComparison.Ordinal)
                    || IsBudgeted(call.Agent.CapabilityProfile));
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Whether a dispatch with this capability profile carries a budget.</summary>
    internal static bool IsBudgeted(string capabilityProfile) => BudgetedProfiles.Contains(capabilityProfile);

    /// <summary>Whether a technology's command allow-list judges this call's command.</summary>
    /// <remarks>
    /// Only a profile that may run commands and may not write files is judged: a developer's commands are its
    /// work, and judging them would refuse every build and test run.
    /// </remarks>
    internal static bool IsCommandJudged(ToolCall call) =>
        call.Command is { Length: > 0 }
        && string.Equals(call.Agent.CapabilityProfile, PlatformReaderProfile, StringComparison.Ordinal);

    /// <summary>
    /// The refusal a shell command earns when it would run in a repository's canonical checkout while a live run of
    /// that repository works in a worktree of its own.
    /// </summary>
    /// <param name="canonicalCheckout">The canonical checkout the command would run in.</param>
    /// <param name="runWorktree">The worktree the live run works in.</param>
    internal static string StrayCommandRefusal(string canonicalCheckout, string runWorktree) =>
        $"{CheckoutRuleId}: this command would run in the repository's canonical checkout {canonicalCheckout} while " +
        $"your run works in {runWorktree}. Start the command with `cd {runWorktree} && ` so every step runs in " +
        "the run worktree; the canonical checkout stays untouched for the whole run.";

    /// <summary>
    /// The directories a command moves into before it acts: every <c>cd</c> or <c>pushd</c> argument and every
    /// <c>git -C</c> argument, as the shell wrote the path. A relative path is resolved against the call's working
    /// directory by the caller, which is the only place that knows it.
    /// </summary>
    /// <remarks>
    /// Every segment is scanned, including the parts after a <c>&amp;&amp;</c>, <c>;</c> or <c>|</c> and inside a
    /// subshell, because a command can step into the canonical checkout at any point. A word only counts as the
    /// program where a program can stand, so a quoted argument that happens to read <c>cd</c> is not mistaken for
    /// one, and quoted text stays a single word, so a path is taken whole. A path the shell expands at run time,
    /// such as a variable, is not one the guard can place, so it is not named here; <see cref="RunsInWorkingDirectory" />
    /// treats that step as running in the working directory instead.
    /// </remarks>
    internal static IReadOnlyList<string> EnteredDirectories(string command) =>
        Walk(command).EnteredDirectories;

    /// <summary>The reading of one command: the directories it enters, and whether a step of it runs in the working directory.</summary>
    private readonly record struct CommandWalk(bool RunsInWorkingDirectory, IReadOnlyList<string> EnteredDirectories);

    /// <summary>The words that end one command and begin the next, kept as words of their own by <see cref="ShellTokens" />.</summary>
    private static readonly string[] CommandSeparators = ["&&", "||", "|", ";", "&", "(", ")"];

    /// <summary>The words that may stand before a program without being one: wrappers, keywords and assignments.</summary>
    private static readonly HashSet<string> CommandPrefixes = new(StringComparer.Ordinal)
    {
        "sudo", "env", "command", "exec", "nohup", "time",
        "if", "then", "else", "elif", "do", "while", "until", "!", "{", "}",
    };

    private static bool IsCommandSeparator(string token) =>
        CommandSeparators.Contains(token, StringComparer.Ordinal);

    private static bool IsCommandPrefix(string token) =>
        CommandPrefixes.Contains(token, StringComparer.Ordinal) || IsAssignment(token);

    private static bool IsChangeDirectory(string token) =>
        string.Equals(token, "cd", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "pushd", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a word names git, however its path is written, including Windows' <c>git.exe</c>.</summary>
    private static bool IsGitProgram(string token)
    {
        string program = Normalise(token);
        int separator = program.LastIndexOf('\\');
        if (separator >= 0)
        {
            program = program[(separator + 1)..];
        }

        return string.Equals(program, "git", StringComparison.OrdinalIgnoreCase)
            || string.Equals(program, "git.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The directory a <c>cd</c> or <c>pushd</c> at <paramref name="index"/> moves into, or null when it names none:
    /// a bare <c>pushd</c> swaps the directory stack, and <c>cd -</c> returns to a directory the command does not
    /// name. The argument it does return may still be one the guard cannot place, such as a variable, which the
    /// caller checks with <see cref="IsPlaceableDirectory" />.
    /// </summary>
    private static string? ChangeDirectoryArgument(string[] tokens, int index)
    {
        for (int next = index + 1; next < tokens.Length && !IsCommandSeparator(tokens[next]); next++)
        {
            if (tokens[next].StartsWith('-'))
            {
                continue;
            }

            return tokens[next];
        }

        return null;
    }

    /// <summary>
    /// Whether any step of the command runs in the call's own working directory, which is the only case where the
    /// caller judges that directory. The walk follows the shell: a <c>cd</c> or <c>pushd</c> naming a placeable
    /// directory moves the shell before later steps, a subshell's changes end at its closing <c>)</c>, a pipeline
    /// stage starts in the directory the pipeline did, and a step after <c>||</c> may run in the directory the
    /// previous step started in. A <c>git</c> step with <c>-C</c> runs in that directory and leaves the shell where
    /// it was. A <c>cd</c> or <c>pushd</c> naming no directory the guard can place, a path it cannot resolve, and
    /// any other step run in the working directory. A command with no step at all reports true, the fail-closed
    /// reading.
    /// </summary>
    /// <remarks>
    /// The walk is the one <see cref="EnteredDirectories" /> reads, so the two always agree on which words are
    /// programs and where each step runs. Every directory the command enters is still judged by
    /// <see cref="EnteredDirectories" />, whether or not this returns false.
    /// </remarks>
    internal static bool RunsInWorkingDirectory(string command) => Walk(command).RunsInWorkingDirectory;

    /// <summary>
    /// Walks the command's steps once in shell order, tracking the directory each step runs in. The working
    /// directory is the initial one, and the value no step can name: a directory change the guard cannot place
    /// falls back to it, so the caller judges the safer path.
    /// </summary>
    private static CommandWalk Walk(string command)
    {
        string[] tokens = ShellTokens(command);
        List<string> entered = [];
        Stack<(string? Current, string? PipelineBase, string? BeforeLastStep, bool InPipeline)> subshells = [];
        bool runsInWorkingDirectory = false;
        bool sawStep = false;
        string? current = null;
        string? pipelineBase = null;
        string? beforeLastStep = null;
        bool inPipeline = false;
        bool commandStart = true;
        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index];
            if (IsCommandSeparator(token))
            {
                if (string.Equals(token, "(", StringComparison.Ordinal))
                {
                    subshells.Push((current, pipelineBase, beforeLastStep, inPipeline));
                    pipelineBase = current;
                    inPipeline = false;
                }
                else if (string.Equals(token, ")", StringComparison.Ordinal))
                {
                    if (subshells.TryPop(out var outer))
                    {
                        (current, pipelineBase, beforeLastStep, inPipeline) = outer;
                    }

                    pipelineBase = current;
                    inPipeline = false;
                }
                else if (string.Equals(token, "|", StringComparison.Ordinal))
                {
                    // A pipeline stage runs in its own subshell from the directory the pipeline began in.
                    current = pipelineBase;
                    inPipeline = true;
                }
                else
                {
                    if (inPipeline)
                    {
                        current = pipelineBase;
                        inPipeline = false;
                    }

                    if (string.Equals(token, "||", StringComparison.Ordinal))
                    {
                        // The right side runs when the left failed, which can leave the shell where the left began.
                        current = beforeLastStep;
                    }

                    pipelineBase = current;
                }

                commandStart = true;
                continue;
            }

            if (!commandStart)
            {
                continue;
            }

            beforeLastStep = current;
            if (IsChangeDirectory(token))
            {
                if (ChangeDirectoryArgument(tokens, index) is { } directory && IsPlaceableDirectory(directory))
                {
                    entered.Add(directory);
                    current = directory;
                }
                else
                {
                    runsInWorkingDirectory = true;
                    current = null;
                }

                sawStep = true;
                commandStart = false;
            }
            else if (IsGitProgram(token))
            {
                if (GitChangeDirectory(tokens, index) is { } directory)
                {
                    if (IsPlaceableDirectory(directory))
                    {
                        entered.Add(directory);
                    }
                    else
                    {
                        // The step names a directory this guard cannot place, so it may run anywhere: judge the cwd.
                        runsInWorkingDirectory = true;
                    }
                }
                else if (current is null)
                {
                    // git with no -C runs where the shell stands.
                    runsInWorkingDirectory = true;
                }

                sawStep = true;
                commandStart = false;
            }
            else if (!IsCommandPrefix(token))
            {
                if (current is null)
                {
                    runsInWorkingDirectory = true;
                }

                sawStep = true;
                commandStart = false;
            }
        }

        return new CommandWalk(runsInWorkingDirectory || !sawStep, entered);
    }

    /// <summary>
    /// Whether the guard can place a directory a command names as written: a path, not a shell expansion such as a
    /// variable, a glob or a home-directory shorthand the command does not spell out.
    /// </summary>
    private static bool IsPlaceableDirectory(string directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && !directory.StartsWith('~')
        && !directory.Contains('$', StringComparison.Ordinal)
        && !directory.Contains('`', StringComparison.Ordinal)
        && !directory.Contains('*', StringComparison.Ordinal)
        && !directory.Contains('?', StringComparison.Ordinal);

    /// <summary>
    /// The directory a <c>git</c> step at <paramref name="index"/> names with <c>-C</c>, or null when it names none.
    /// Only git's global options, which come before its subcommand, are read, so a subcommand's own <c>-C</c> is
    /// never taken for a directory change.
    /// </summary>
    private static string? GitChangeDirectory(string[] tokens, int index)
    {
        for (int next = index + 1; next < tokens.Length && !IsCommandSeparator(tokens[next]); next++)
        {
            string option = tokens[next];
            if (!option.StartsWith('-') || string.Equals(option, "--", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(option, "-C", StringComparison.Ordinal))
            {
                return next + 1 < tokens.Length && !IsCommandSeparator(tokens[next + 1])
                    ? tokens[next + 1]
                    : null;
            }

            if (option.StartsWith("-C", StringComparison.Ordinal) && option.Length > 2)
            {
                return option[2..].TrimStart('=');
            }

            if (GitOptionTakesValue(option))
            {
                next++;
            }
        }

        return null;
    }

    /// <summary>The git global options that take a separate value, so the word after one is not a subcommand.</summary>
    private static bool GitOptionTakesValue(string option) =>
        option is "-c" or "--git-dir" or "--work-tree" or "--namespace" or "--exec-path" or "--super-prefix"
            or "--config-env" or "--attr-source";

    /// <summary>
    /// The command split into words: quotes are honoured and removed, and an unquoted join, redirect or newline
    /// becomes a word of its own, so the scan can stop one command where the next begins.
    /// </summary>
    private static string[] ShellTokens(string command)
    {
        List<string> tokens = [];
        StringBuilder word = new();
        bool singleQuoted = false;
        bool doubleQuoted = false;
        for (int index = 0; index < command.Length; index++)
        {
            char current = command[index];
            if (singleQuoted)
            {
                if (current == '\'')
                {
                    singleQuoted = false;
                }
                else
                {
                    word.Append(current);
                }

                continue;
            }

            if (doubleQuoted)
            {
                if (current == '\\'
                    && index + 1 < command.Length
                    && command[index + 1] is '"' or '\\')
                {
                    word.Append(command[++index]);
                }
                else if (current == '"')
                {
                    doubleQuoted = false;
                }
                else
                {
                    word.Append(current);
                }

                continue;
            }

            switch (current)
            {
                case '\'':
                    singleQuoted = true;
                    break;
                case '"':
                    doubleQuoted = true;
                    break;
                case ' ' or '\t':
                    FlushWord(tokens, word);
                    break;
                case ';' or '\n' or '\r':
                    FlushWord(tokens, word);
                    tokens.Add(";");
                    break;
                case '(' or ')':
                    FlushWord(tokens, word);
                    tokens.Add(current.ToString());
                    break;
                case '|':
                    FlushWord(tokens, word);
                    if (index + 1 < command.Length && command[index + 1] == '|')
                    {
                        index++;
                        tokens.Add("||");
                    }
                    else
                    {
                        tokens.Add("|");
                    }

                    break;
                case '&':
                    FlushWord(tokens, word);
                    if (index + 1 < command.Length && command[index + 1] == '&')
                    {
                        index++;
                        tokens.Add("&&");
                    }
                    else
                    {
                        tokens.Add("&");
                    }

                    break;
                default:
                    word.Append(current);
                    break;
            }
        }

        FlushWord(tokens, word);
        return [.. tokens];
    }

    private static void FlushWord(List<string> tokens, StringBuilder word)
    {
        if (word.Length > 0)
        {
            tokens.Add(word.ToString());
            word.Clear();
        }
    }

    /// <summary>
    /// The refusal a technology's command allow-list makes, or null when no rule refuses this command. A rule
    /// loaded from a technology folder judges only a task of that technology, so the caller passes the technology
    /// the read-only dispatch named. A dispatch that names none, or one the caller cannot identify, passes null,
    /// which applies every loaded technology's list: the fail-closed reading. Each rule still narrows itself to
    /// its own tool's commands.
    /// </summary>
    /// <param name="rules">Every rule the configuration loaded.</param>
    /// <param name="call">The sub-agent's tool call.</param>
    /// <param name="command">The command the call declares.</param>
    /// <param name="technologyScope">The technology whose rules apply, or null for every rule.</param>
    /// <param name="parents">Each technology folder's <c>builds-on</c> parent, so a parent's list reaches its child.</param>
    internal static string? CommandRefusal(
        IReadOnlyList<ArbiterRule> rules,
        ToolCall call,
        string command,
        IReadOnlyCollection<string>? technologyScope = null,
        IReadOnlyDictionary<string, string>? parents = null)
    {
        ArgumentNullException.ThrowIfNull(rules);

        IReadOnlyList<ArbiterRule> scoped = ArbiterRuleScopes.Applicable(rules, technologyScope, parents);
        ArbiterOutcome outcome = RuleEngine.EvaluateStep0(scoped, CommandFacts(call, command), CommandTrigger);
        if (!string.Equals(outcome.CombinedEffect, RuleEffects.Escalate, StringComparison.Ordinal))
        {
            return null;
        }

        string refusing = string.Join(", ", outcome.RuleAnswers
            .Where(answer => !answer.Shadow
                && string.Equals(answer.Effect, RuleEffects.Escalate, StringComparison.Ordinal))
            .Select(answer => $"{answer.RuleId} ({answer.Answer})")
            .Distinct(StringComparer.Ordinal));
        return $"{refusing}: this command is outside the forms this technology allows a read-only agent to run " +
            $"('{ShapeOf(command).Form}'). Nothing ran. Use a listed read form, or report what you could not inspect.";
    }

    /// <summary>The facts a <c>worker.command</c> rule reads, from the call the harness reported.</summary>
    internal static ArbiterFactSet CommandFacts(ToolCall call, string command)
    {
        CommandShape shape = ShapeOf(command);
        return new ArbiterFactSet()
            .With(WorkerNameFact, call.Agent.Name, ArbiterFactLabel.Derived)
            .With(WorkerProfileFact, call.Agent.CapabilityProfile, ArbiterFactLabel.Derived)
            .With(WorkerHarnessFact, call.Harness, ArbiterFactLabel.Derived)
            .With(CommandTextFact, shape.Text, ArbiterFactLabel.Asserted)
            .With(CommandFormFact, shape.Form, ArbiterFactLabel.Asserted)
            .With(CommandSingleFact, shape.Single, ArbiterFactLabel.Derived)
            .With(CommandHeadFact, shape.Head, ArbiterFactLabel.Asserted)
            .With(CommandProgramFact, shape.Program, ArbiterFactLabel.Asserted)
            .With(CommandWordsFact, shape.Words, ArbiterFactLabel.Asserted);
    }

    /// <summary>A command's form, whether it is one command, its head words, its program, and every word it names.</summary>
    /// <param name="Text">The command as reported, whitespace collapsed.</param>
    /// <param name="Form">The single form's program and leading words, or the whole text when there is no single form.</param>
    /// <param name="Single">Whether the command is one command, with no join, redirect, subshell or substitution.</param>
    /// <param name="Head">The form's words before its first option, where a mutating verb appears; empty when not one command.</param>
    /// <param name="Program">The form's first word, or null when the command has no single form.</param>
    /// <param name="Words">Every word the command names, with quotes and wrapping punctuation removed and a path's directory stripped.</param>
    private sealed record CommandShape(
        string Text,
        string Form,
        bool Single,
        IReadOnlyList<string> Head,
        string? Program,
        IReadOnlyList<string> Words);

    /// <summary>
    /// Reads one command into the shape a rule judges. A single command yields its form and its head; a compound
    /// command, a redirect, a subshell, a grouping or a substitution yields its whole text, no head and no program,
    /// because it is not one form and no listed form may allow it. Its words still name every tool it reaches, which
    /// is how a technology's rule refuses it.
    /// </summary>
    private static CommandShape ShapeOf(string command)
    {
        string text = Collapse(command);
        IReadOnlyList<string> words = WordsOf(text);
        if (!IsSingleCommand(command))
        {
            return new CommandShape(text, text, Single: false, Head: [], Program: null, words);
        }

        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int start = 0;
        while (start < tokens.Length
               && (CommandWrappers.Contains(tokens[start]) || IsAssignment(tokens[start])))
        {
            start++;
        }

        List<string> leading = [];
        for (int index = start; index < tokens.Length; index++)
        {
            if (tokens[index].StartsWith('-'))
            {
                break;
            }

            leading.Add(tokens[index]);
        }

        string form = leading.Count == 0 ? text : string.Join(' ', leading);
        return new CommandShape(
            text,
            form,
            Single: true,
            Head: leading,
            Program: leading.Count > 0 ? Normalise(leading[0]) : null,
            words);
    }

    /// <summary>
    /// Whether the command is exactly one command: no unquoted character joins, redirects, groups or substitutes,
    /// and no <c>$(</c> outside single quotes. Quotes are honoured so a query such as <c>--query "{a:b}"</c> is still
    /// one command.
    /// </summary>
    private static bool IsSingleCommand(string command)
    {
        bool singleQuoted = false;
        bool doubleQuoted = false;
        for (int index = 0; index < command.Length; index++)
        {
            char current = command[index];
            if (singleQuoted)
            {
                if (current == '\'')
                {
                    singleQuoted = false;
                }

                continue;
            }

            if (current == '\'')
            {
                singleQuoted = true;
                continue;
            }

            if (doubleQuoted)
            {
                // A substitution runs inside double quotes, so only the closing quote ends the literal run.
                if (current == '`'
                    || (current == '$' && index + 1 < command.Length && command[index + 1] == '('))
                {
                    return false;
                }

                if (current == '"')
                {
                    doubleQuoted = false;
                }

                continue;
            }

            if (current == '"')
            {
                doubleQuoted = true;
                continue;
            }

            if (ShellControl.Contains(current)
                || (current == '$' && index + 1 < command.Length && command[index + 1] == '('))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Every word the command names, for a rule that must see the tool however it was reached: a quoted program, an
    /// absolute path, a word inside a compound command, or a token still carrying a subshell's bracket.
    /// </summary>
    private static IReadOnlyList<string> WordsOf(string text)
    {
        List<string> words = [];
        foreach (string token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string word = Normalise(token);
            if (word.Length > 0)
            {
                words.Add(word);
            }
        }

        return words;
    }

    /// <summary>
    /// One word reduced to the name a rule can match: quotes and the shell's wrapping punctuation removed, and a
    /// path's directory stripped so <c>/usr/bin/az</c> is <c>az</c>.
    /// </summary>
    private static string Normalise(string token)
    {
        string trimmed = token.Trim(WordWrapping);
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static bool IsAssignment(string token)
    {
        int equals = token.IndexOf('=', StringComparison.Ordinal);
        return equals > 0 && token[..equals].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static string Collapse(string command) =>
        string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The one-time deny after a call that started a process, or waited on one, and ran past the per-call limit. A
    /// call that starts no process (a file read) cannot hang, so the limit never judges it. The denied call is counted
    /// like any other, so the retry that follows comes right after it and is allowed.
    /// </summary>
    internal static string StalledCallRefusal(TimeSpan gap, int limit) =>
        $"{BudgetRuleId}: your previous tool call ran for about {(int)gap.TotalMinutes} minutes, past the {limit}-minute " +
        "limit for one call. If it started a process that is still running (a hung test run, server or watcher), stop " +
        "that process now. Record NOT VERIFIED: <the command> — timed out in your report, do not run it again unchanged, " +
        "and move on to the rest of your task. This call was denied once; make it again to continue.";

    internal static bool WindowEnded(string[] lines, DateTimeOffset now)
    {
        if (Window.Read(lines) is not { } window)
        {
            // An empty file is one another hook process is still creating; anything else unreadable starts over.
            return lines.Length > 0;
        }

        TimeSpan idle = now - window.LastCall;
        return idle > TimeSpan.FromMinutes(window.Minutes) || (window.IsOver(window.LastCall) && idle > TimeSpan.FromMinutes(1));
    }

    internal static string KeyOf(string harness, string agentId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(harness + "\n" + agentId)))[..24];

    /// <summary>The refusal an exhausted budget answers with.</summary>
    internal static string ExhaustedRefusal(bool seat, bool writesSeatFile, int calls, TimeSpan elapsed, int maxCalls, int minutes)
    {
        // A seat past its budget still writes its seat file, so the partial result it was told to write is not lost.
        if (seat)
        {
            return writesSeatFile
                ? string.Empty
                : $"{BudgetRuleId}: this review seat has made {calls} tool calls in {(int)elapsed.TotalMinutes} minutes, past " +
                  $"its budget of {maxCalls} calls or {minutes} minutes. Make no more reads or lookups. Return your output now " +
                  "from what you have: as a lens seat, the findings you can support, then, among the closing lines, " +
                  $"'{SeatBudgetLine} <what your lens did not reach>'; as a refutation, SUPPORTED for every finding you " +
                  "have not defeated with evidence. If your invocation names a Seat file, write it there (that write is " +
                  "allowed); otherwise reply with it.";
        }

        return $"{BudgetRuleId}: this dispatch has made {calls} tool calls in {(int)elapsed.TotalMinutes} minutes, past its " +
               $"budget of {maxCalls} calls or {minutes} minutes. Stop now: make no more tool calls. Return a report whose " +
               $"first line is {BudgetExceededStatus}, then what is done, what is not, and the last failure you saw.";
    }

    /// <summary>
    /// The per-call limit the guard applies: the configured one, or the longest task gate the repository declares plus a
    /// minute for the model's own turn, whichever is longer. The runner stops a gate at its own timeout, so a limit
    /// below that would call a healthy gate a hung process.
    /// </summary>
    internal static int EffectiveCallMinutes(ArbiterDispatchBudgetConfig budget, ReviewConfig? review)
    {
        int longestSeconds = 0;
        if (review is not null)
        {
            foreach (IReadOnlyList<ReviewTaskGate> gates in review.TaskGates.Values.Concat(review.TechnologyGates.Values))
            {
                foreach (ReviewTaskGate gate in gates)
                {
                    longestSeconds = Math.Max(longestSeconds, gate.TimeoutSeconds);
                }
            }
        }

        if (longestSeconds <= 0)
        {
            return budget.CallMinutes;
        }

        int gateMinutes = (longestSeconds + 59) / 60 + 1;
        return Math.Max(budget.CallMinutes, gateMinutes);
    }

    /// <summary>The header line a budget file opens with.</summary>
    internal static string HeaderLine(DateTimeOffset now, ArbiterDispatchBudgetConfig budget, string agentName) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{now.ToUnixTimeMilliseconds()} {budget.Minutes} {budget.ToolCalls} {agentName} {budget.CallMinutes}\n");

    /// <summary>The line a call appends to a budget file: its timestamp and whether the call started a process.</summary>
    internal static string CallLine(DateTimeOffset now, bool startedProcess) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{now.ToUnixTimeMilliseconds()} {(startedProcess ? ProcessCallMarker : NoProcessCallMarker)}\n");

    internal static string Deny(string harness, string reason) =>
        string.Equals(harness, ClaudeHarness, StringComparison.Ordinal)
            ? new JsonObject
            {
                ["hookSpecificOutput"] = new JsonObject
                {
                    ["hookEventName"] = "PreToolUse",
                    ["permissionDecision"] = "deny",
                    ["permissionDecisionReason"] = reason,
                },
            }.ToJsonString()
            : new JsonObject
            {
                ["permissionDecision"] = "deny",
                ["permissionDecisionReason"] = reason,
            }.ToJsonString();

    internal static string OneLine(string message) =>
        message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>
    /// One budget window as its file records it: the header, then one line per call holding its timestamp and a marker
    /// that says whether the call started a process.
    /// </summary>
    internal sealed record Window(
        DateTimeOffset Start,
        int Minutes,
        int MaxCalls,
        int Calls,
        DateTimeOffset LastCall,
        int CallMinutes,
        bool LastCallStartedProcess)
    {
        public bool IsOver(DateTimeOffset at) => Calls > MaxCalls || at - Start > TimeSpan.FromMinutes(Minutes);

        public static Window? Read(string[] lines)
        {
            string[] header = lines.Length > 0 ? lines[0].Split(' ') : [];
            if (header.Length < 3
                || !long.TryParse(header[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long startMs)
                || !int.TryParse(header[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
                || !int.TryParse(header[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxCalls))
            {
                return null;
            }

            // A header written before the per-call limit existed has four fields; it gets the shipped limit.
            int callMinutes = header.Length >= 5
                && int.TryParse(header[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCall)
                && parsedCall > 0
                    ? parsedCall
                    : ArbiterDispatchBudgetConfig.Defaults.CallMinutes;

            long lastMs = startMs;
            bool lastStartedProcess = true;
            if (lines.Length > 1 && TryReadCall(lines[^1], out long parsed, out bool startedProcess))
            {
                lastMs = parsed;
                lastStartedProcess = startedProcess;
            }

            return new Window(
                DateTimeOffset.FromUnixTimeMilliseconds(startMs),
                minutes,
                maxCalls,
                lines.Length - 1,
                DateTimeOffset.FromUnixTimeMilliseconds(lastMs),
                callMinutes,
                lastStartedProcess);
        }

        /// <summary>
        /// Reads one call line into its timestamp and whether the call started a process. A line written before the
        /// marker existed carries a bare timestamp: it counts as a process call, which is how that file was judged then.
        /// </summary>
        private static bool TryReadCall(string line, out long milliseconds, out bool startedProcess)
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            milliseconds = 0;
            startedProcess = fields.Length < 2 || !string.Equals(fields[1], NoProcessCallMarker, StringComparison.Ordinal);
            return fields.Length > 0
                && long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds);
        }
    }

    /// <summary>
    /// One guarded tool call. <paramref name="Command"/> is the shell command the call carries, which other rules
    /// match on; <paramref name="StartedProcess"/> is true for a call that runs a process or waits on a running one,
    /// which is what the per-call limit is judged on. <paramref name="ReadPath"/> is the path a harness's own read or
    /// view tool names, which the secret rule judges like a file a command would read.
    /// </summary>
    internal sealed record ToolCall(
        string Harness,
        string AgentId,
        ArbiterSquadAgent Agent,
        string? Cwd,
        string? WritePath,
        string? Command,
        bool StartedProcess,
        string? SessionId = null,
        string? ReadPath = null);
}
