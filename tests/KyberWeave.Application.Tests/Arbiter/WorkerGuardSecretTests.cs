using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Data.Hooks.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-SECRET-001</c> denies a Squad subagent's call that would put a secret into the transcript: a command
/// that reads a secret file or a read of it through the harness's own view tool, a command that prints a secret from
/// a store, and a command that carries a literal secret. The guard, its fakes and the harness event shapes are the
/// real ones over an in-memory file system, so this is the same decision the hook makes with no repository on disk.
/// Every value here is fake: no row names a real credential.
/// </summary>
public sealed class WorkerGuardSecretTests
{
    private const string RuleId = "KW-ARB-SECRET-001";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeProcessEnvironment _environment;
    private readonly FileWorkerSessionStore _sessions;
    private readonly string _home;
    private readonly string _repo;

    public WorkerGuardSecretTests()
    {
        _home = Path.Combine(_files.Root, "kw", "home");
        _repo = Path.Combine(_files.Root, "kw", "repo");
        _files.CreateDirectory(_home);
        _files.CreateDirectory(_repo);
        _sessions = new FileWorkerSessionStore(_files);
        _environment = new FakeProcessEnvironment(
            currentDirectory: _repo,
            homeDirectory: _home,
            isFileSystemCaseInsensitive: !_files.CaseSensitive);
    }

    /// <summary>
    /// Every call the secret rule judges. <c>tool</c> is <c>shell</c>, <c>copilot-read</c> or <c>claude-read</c>;
    /// <c>call</c> is the shell command or the read path; <c>allowed</c> is the expected answer.
    /// </summary>
    public static TheoryData<string, string, bool> CallsJudgedByTheSecretRule => new()
    {
        // 1. A shell command that reads a secret file: a secret-named file, a dotenv file, a private directory,
        //    an extension that carries a key, and each reader shape the rule names.
        { "shell", "cat ~/.config/hotshot-logistics/sa-password", false },
        { "shell", "head -n 5 ~/.aws/credentials", false },
        { "shell", "tail -n 20 ~/.ssh/id_ed25519", false },
        { "shell", "less .env", false },
        { "shell", "more ~/.netrc", false },
        { "shell", "type ~/.git-credentials", false },
        { "shell", "Get-Content ~/.kube/config", false },
        { "shell", "gc ~/.npmrc", false },
        { "shell", "bat .env.local", false },
        { "shell", "xxd server.pem", false },
        { "shell", "strings client.p12", false },
        { "shell", "base64 ~/.ssh/id_rsa", false },
        { "shell", "cp ~/.aws/credentials /tmp/out", false },
        { "shell", "scp ~/.ssh/id_ed25519 build-host:/tmp/out", false },
        { "shell", "python -c \"print(open('.env').read())\"", false },
        { "shell", "bash < ~/.aws/credentials", false },
        { "shell", "cat ~/.microsoft/usersecrets/abc/secrets.json", false },
        { "shell", "cat ~/.microsoft/usersecrets/abc/appsettings.json", false },
        { "shell", "cat ~/.azure/msal_token_cache.json", false },
        { "shell", "cat ~/.azure/clouds.config", false },
        { "shell", "cat ~/.docker/config.json", false },
        { "shell", "cat ~/.pypirc", false },
        { "shell", "cat deploy.key", false },
        { "shell", "cat ~/.config/hotshot-logistics/api-key.txt", false },

        // 1. A secret fragment in a source or documentation file's name does not make it a secret: the fragment
        //    test covers a file with no extension, or a configuration or data extension, only.
        { "copilot-read", "src/KyberWeave.Application/Arbiter/Hooks/SecretRules.cs", true },
        { "shell", "cat src/Auth/TokenService.cs", true },
        { "copilot-read", "docs/security/secret-handling.md", true },
        { "shell", "head -n 40 tests/PasswordHasherTests.cs", true },
        { "copilot-read", "config/db-password.txt", false },
        { "shell", "cat appsettings.secrets.json", false },
        { "shell", "cat ~/.config/x/sa-password", false },

        // 1. A heredoc counts only when the word after a single `<` is a secret file, and an open() only when the
        //    file it names is.
        { "shell", "cat > notes.md <<'EOF'\nreset the password weekly\nEOF", true },
        { "shell", "python3 - <<'EOF'\nprint('token count')\nEOF", true },
        { "shell", "python3 -c \"open('README.md').read()\"", true },
        { "shell", "sqlcmd -i query.sql < ~/.config/x/sa-password", false },
        { "shell", "python3 -c \"print(open('/home/u/.aws/credentials').read())\"", false },

        // 1. Every reading program that names a file, including the search, text and JSON tools.
        { "shell", "grep . ~/.config/x/sa-password", false },
        { "shell", "sed -n 1p .env", false },
        { "shell", "awk 1 ~/.aws/credentials", false },
        { "shell", "jq . config/api-token.json", false },
        { "shell", "dd if=~/.ssh/id_ed25519", false },
        { "shell", "Select-String -Path .env -Pattern x", false },
        { "shell", "grep -rn \"Password=\" src/", true },
        { "shell", "jq . package.json", true },

        // 1. A read through the harness's own view tool carries the path, so the rule sees it too.
        { "copilot-read", "~/.config/hotshot-logistics/sa-password", false },
        { "claude-read", "~/.config/hotshot-logistics/sa-password", false },

        // 1. Wrapping the command in a shell escapes nothing: the whole text is matched, quotes included.
        { "shell", "bash -c \"cat ~/.config/a/sa-password\"", false },
        { "shell", "pwsh -Command \"Get-Content ~/.aws/credentials\"", false },

        // 2. The same holds for a store read inside a shell string: the inner command still stands where one can.
        { "shell", "bash -c \"printenv\"", false },
        { "shell", "sh -c 'env'", false },

        // 1. Listed, published and example files, and testing whether a file exists, are allowed.
        { "shell", "ls ~/.ssh", true },
        { "shell", "cat ~/.ssh/id_ed25519.pub", true },
        { "shell", "cat ~/.env.example", true },
        { "shell", "cat .env.sample", true },
        { "shell", "cat ~/.ssh/config", true },
        { "shell", "cat ~/.ssh/known_hosts", true },
        { "shell", "test -f ~/.config/x/sa-password", true },
        { "copilot-read", "src/Job.cs", true },

        // 2. A command that prints a secret from a store.
        { "shell", "dotnet user-secrets list", false },
        { "shell", "docker inspect app", false },
        { "shell", "docker inspect --format '{{.Config.Env}}' app", false },
        { "shell", "docker exec app env", false },
        { "shell", "env", false },
        { "shell", "printenv", false },
        { "shell", "printenv DB_PASSWORD", false },
        { "shell", "echo $DB_PASSWORD", false },
        { "shell", "echo ${API_TOKEN}", false },
        { "shell", "echo $env:AZURE_CLIENT_SECRET", false },
        { "shell", "printf \"%s\" \"$GITHUB_TOKEN\"", false },
        { "shell", "Write-Output $env:DB_PASSWORD", false },
        { "shell", "Write-Host ${DB_PASSWORD}", false },
        { "shell", "Get-ChildItem env:", false },
        { "shell", "gci env:", false },
        { "shell", "dir env:", false },
        { "shell", "security find-generic-password -w -s db", false },
        { "shell", "security find-internet-password -g -s db", false },
        { "shell", "gh auth token", false },
        { "shell", "az account get-access-token", false },
        { "shell", "kubectl get secret db -o yaml", false },
        { "shell", "kubectl get secret db -o jsonpath={.data}", false },
        { "shell", "cat /proc/1/environ", false },
        { "shell", "python -c \"import os; print(os.environ)\"", false },
        { "shell", "python -c \"import os; print(os.environ['DB_PASSWORD'])\"", false },
        { "shell", "node -e \"console.log(process.env)\"", false },

        // 2. A store read that prints only a non-secret field, and a variable that is not secret-named, are allowed.
        { "shell", "docker inspect -f '{{.State.Health.Status}}' app", true },
        { "shell", "echo $HOME", true },
        { "shell", "printenv PATH", true },

        // 3. A command that puts a literal secret on the command line.
        { "shell", "docker exec -e SQLCMDPASSWORD=Fake-Pa55 app true", false },
        { "shell", "docker run --env DB_PASSWORD=Fake-Pa55 img", false },
        { "shell", "DB_TOKEN=Fake-Pa55 dotnet test", false },
        { "shell", "sqlcmd -S db -U sa -P Fake-Pa55", false },
        { "shell", "mysql -u root -pFake-Pa55 app", false },
        { "shell", "mysql --password=Fake-Pa55 -u root app", false },
        { "shell", "curl -u dave:Fake-Pa55 https://example.test", false },
        { "shell", "curl --user dave:Fake-Pa55 https://example.test", false },
        { "shell", "curl -H \"Authorization: Bearer Fake-Pa55\" https://example.test", false },
        { "shell", "dotnet user-secrets set \"ConnectionStrings:X\" \"Server=.;Password=Fake-Pa55\"", false },
        { "shell", "sqlcmd -S db \"Server=.;Pwd=Fake-Pa55\"", false },

        // 3. A value passed by reference never enters the transcript, so it is allowed.
        {
            "shell",
            "docker exec app sh -c 'SQLCMDPASSWORD=\"$MSSQL_SA_PASSWORD\" sqlcmd -S db -U sa'",
            true
        },
        { "shell", "mysql -u root -p app", true },

        // The ordinary work a worker does is untouched.
        { "shell", "grep -rn password src/", true },
        { "shell", "git log", true },
        { "shell", "dotnet test", true },
    };

    /// <summary>
    /// The calls the rule reads precisely: grep and rg options are parsed so an option's value is neither the
    /// pattern nor a file, a known interpreter's heredoc body is judged as a script rather than as shell steps, and
    /// <c>open(...)</c> and <c>Path(...)</c> fail closed on every literal they could open. Every real secret read is
    /// still refused, including the forms the old reading caught only by accident.
    /// </summary>
    public static TheoryData<string, string, bool> CallsJudgedPreciselyByTheSecretRule => new()
    {
        // A grep or rg option's value is not the pattern, and a quoted pattern is one word.
        { "shell", "grep -A 3 token src/a.cs", true },
        { "shell", "grep -nA 3 token src/a.cs", true },
        { "shell", "grep -A3 token src/a.cs", true },
        { "shell", "grep -rn --include '*.cs' token src", true },
        { "shell", "rg -g '*.yml' token", true },
        { "shell", "rg -e token -n src", true },
        { "shell", "grep -n \"bearer token\" src/a.cs", true },
        { "shell", "bash -c \"grep -A 3 token src/a.cs\"", true },

        // A search that names a secret file is still refused, wherever the file stands.
        { "shell", "grep -A 3 x .env", false },
        { "shell", "grep \"x\" \".env\"", false },
        { "shell", "rg -e x secrets.json", false },
        { "shell", "grep -- x .env", false },
        { "shell", "grep x .env -n", false },
        { "shell", "grep -r x --include .env .", false },
        { "shell", "rg x -g .env", false },
        { "shell", "bash -c \"grep -A 3 x .env\"", false },
        { "shell", "sh -c 'grep x secrets.json'", false },
        { "shell", "ls | xargs grep x .env", false },

        // Every option value that names a file is judged, in a bundle or attached too.
        { "shell", "grep -f .env x", false },
        { "shell", "grep -nf .env x", false },
        { "shell", "grep -f.env x", false },
        { "shell", "grep -nf.env x", false },
        { "shell", "grep -f pats.txt .env", false },
        { "shell", "grep --file=secrets.json x", false },
        { "shell", "grep --file secrets.json x", false },
        { "shell", "grep --exclude-from=.env x", false },
        { "shell", "grep --include-from .env x", false },
        { "shell", "rg --ignore-file .env x", false },

        // Nested shell text and plain readers are matched as before.
        { "shell", "bash -c \"cat .env\"", false },
        { "shell", "eval \"cat token.txt\"", false },
        { "shell", "cat token.txt", false },

        // A known interpreter's heredoc body is a script: its lines are not shell readers.
        { "shell", "python3 - <<'EOF'\nhead = token_count[:3]\nprint(head)\nEOF", true },
        { "shell", "python3 - <<'EOF'\nsort( token_list )\nEOF", true },
        { "shell", "python3 - <<'EOF'\ntype (token)\nEOF", true },
        {
            "shell",
            "python3 - <<'EOF'\ntoken_path = 'out/report.txt'\nwith open(token_path, 'w') as f:\n    f.write('done')\nEOF",
            true
        },

        // Any other program's heredoc keeps its lines as shell steps, and a script body is still judged.
        { "shell", "bash <<'EOF'\ncat .env\nEOF", false },
        { "shell", "mytool <<'EOF'\ncat .env\nEOF", false },
        { "shell", "ssh build-host python3 <<'EOF'\ncat .env\nEOF", false },
        { "shell", "python3 - <<'EOF'\nprint('x')\nEOF\ncat .env", false },
        { "shell", "python3 - <<'EOF'\ncat .env", false },
        { "shell", "python3 <<EOF\nprint(\"$(cat .env)\")\nEOF", false },
        { "shell", "python3 - <<'EOF'\nimport subprocess\nsubprocess.run('cat .env', shell=True)\nEOF", false },
        { "shell", "python3 - <<'EOF'\nimport os\nprint(os.environ)\nEOF", false },
        { "shell", "python3 - <<'EOF'\np = '.env'\nprint(open(p).read())\nEOF", false },

        // open(...) and Path(...) judge every literal they could open, and every value an identifier is given.
        { "shell", "python3 -c \"open('.env').read()\"", false },
        { "shell", "python3 -c \"open(os.path.join(home, '.aws', 'credentials')).read()\"", false },
        { "shell", "python3 -c \"open(d + '/.env').read()\"", false },
        { "shell", "python3 -c 'open(f\"{d}/.env\").read()'", false },
        { "shell", "python3 -c \"open(Path('.env')).read()\"", false },
        { "shell", "python3 -c \"from pathlib import Path; Path('.env').read_text()\"", false },
        { "shell", "python3 -c \"p = 'out.txt'; p = '.env'; print(open(p).read())\"", false },
    };

    [Theory]
    [MemberData(nameof(CallsJudgedByTheSecretRule))]
    [MemberData(nameof(CallsJudgedPreciselyByTheSecretRule))]
    public void EverySecretCallIsJudged(string tool, string call, bool allowed)
    {
        (string harness, string json) = tool switch
        {
            "copilot-read" => ("copilot-cli", CopilotRead(call)),
            "claude-read" => ("claude", ClaudeRead(call)),
            _ => ("copilot-cli", Copilot(_repo, call)),
        };

        string answer = Guard(harness, json);

        if (allowed)
        {
            Assert.Empty(answer);
            return;
        }

        string reason = DenialReason(answer);
        Assert.StartsWith(RuleId, reason, StringComparison.Ordinal);
        Assert.Contains("Never read or print a password, key or token.", reason, StringComparison.Ordinal);
        Assert.Contains("would put a secret into the transcript", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("echo $(cat ~/.ssh/id_rsa)")]
    [InlineData("echo \"$(cat ~/.ssh/id_rsa)\"")]
    [InlineData("echo `cat ~/.ssh/id_rsa`")]
    public void ACommandSubstitutionThatReadsASecretFileIsRefused(string command)
    {
        string answer = Guard("copilot-cli", Copilot(_repo, command));

        Assert.StartsWith(RuleId, DenialReason(answer), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("echo $password")]
    [InlineData("echo $PASSWORD")]
    [InlineData("echo $TOKEN")]
    [InlineData("echo $token")]
    [InlineData("echo $MY_TOKEN")]
    [InlineData("echo ${env:API_KEY}")]
    public void ASecretVariableNamedByItsKeywordAloneIsRefused(string command)
    {
        string answer = Guard("copilot-cli", Copilot(_repo, command));

        Assert.StartsWith(RuleId, DenialReason(answer), StringComparison.Ordinal);
    }

    [Fact]
    public void AHerestringOfASecretVariableIsRefused()
    {
        string answer = Guard("copilot-cli", Copilot(_repo, "cat <<< \"$SECRET_TOKEN\""));

        Assert.StartsWith(RuleId, DenialReason(answer), StringComparison.Ordinal);
    }

    [Fact]
    public void AFactoryChildsSecretCommandIsRefusedWithFactorysDenyDocument()
    {
        FactoryChild();

        string answer = Guard("factory", FactoryExecute("cat ~/.config/x/sa-password"));

        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement output = document.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.StartsWith(RuleId, output.GetProperty("permissionDecisionReason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFactoryChildsReadOfASecretFileIsRefused()
    {
        FactoryChild();

        string answer = Guard("factory", FactoryRead("/home/operator/.config/x/sa-password"));

        Assert.StartsWith(RuleId, DenialReason(answer), StringComparison.Ordinal);
    }

    [Fact]
    public void AFactoryRootSessionsSecretCommandIsNeverGuarded()
    {
        Guard("factory", JsonSerializer.Serialize(new
        {
            session_id = "root-1",
            hook_event_name = "SessionStart",
            source = "startup",
            cwd = _repo,
        }));

        Assert.Empty(Guard("factory", FactoryExecute("cat ~/.config/x/sa-password", sessionId: "root-1")));
    }

    private void FactoryChild()
    {
        Guard("factory", JsonSerializer.Serialize(new
        {
            session_id = "root-1",
            hook_event_name = "SessionStart",
            source = "startup",
            cwd = _repo,
        }));
        Guard("factory", JsonSerializer.Serialize(new
        {
            session_id = "root-1",
            hook_event_name = "PreToolUse",
            tool_name = "Task",
            tool_input = new { subagent_type = "dev-medium", description = "work", prompt = "do it" },
            cwd = _repo,
        }));
        Guard("factory", JsonSerializer.Serialize(new
        {
            session_id = "child-1",
            hook_event_name = "SessionStart",
            source = "startup",
            calling_session_id = "root-1",
            cwd = _repo,
        }));
    }

    private static string FactoryExecute(string command, string sessionId = "child-1") => JsonSerializer.Serialize(new
    {
        session_id = sessionId,
        hook_event_name = "PreToolUse",
        tool_name = "Execute",
        tool_input = new { command, summary = "run", riskLevel = "low" },
        cwd = "/kw/repo",
    });

    private static string FactoryRead(string path) => JsonSerializer.Serialize(new
    {
        session_id = "child-1",
        hook_event_name = "PreToolUse",
        tool_name = "Read",
        tool_input = new { file_path = path, line_numbers = true },
        cwd = "/kw/repo",
    });

    private string Guard(string harness, string json)
    {
        WorkerGuard guard = new(
            new FileWorkerBudgetStore(_files),
            new NoCheckouts(),
            new NoConfig(),
            _environment,
            TimeProvider.System,
            new NoState(),
            _files,
            _sessions);
        using StringWriter stdout = new();
        guard.Run(harness, json, stdout, TextWriter.Null, _home);
        return stdout.ToString();
    }

    private static string DenialReason(string answer)
    {
        Assert.NotEmpty(answer);
        using JsonDocument document = JsonDocument.Parse(answer);
        JsonElement decision = document.RootElement.TryGetProperty("hookSpecificOutput", out JsonElement nested)
            ? nested
            : document.RootElement;
        Assert.Equal("deny", decision.GetProperty("permissionDecision").GetString());
        return decision.GetProperty("permissionDecisionReason").GetString()!;
    }

    private static string Copilot(string cwd, string command) => JsonSerializer.Serialize(new
    {
        cwd,
        agentId = "agent-7",
        parentAgentId = "root",
        agentType = "architect",
        toolName = "bash",
        toolArgs = JsonSerializer.Serialize(new { command }),
    });

    private static string CopilotRead(string path) => JsonSerializer.Serialize(new
    {
        cwd = "/kw/repo",
        agentId = "agent-7",
        parentAgentId = "root",
        agentType = "architect",
        toolName = "view",
        toolArgs = JsonSerializer.Serialize(new { path }),
    });

    private static string ClaudeRead(string path) => JsonSerializer.Serialize(new
    {
        cwd = "/kw/repo",
        session_id = "sess-1",
        hook_event_name = "PreToolUse",
        agent_id = "agent-7",
        agent_type = "architect",
        tool_name = "Read",
        tool_input = new { file_path = path },
    });
}
