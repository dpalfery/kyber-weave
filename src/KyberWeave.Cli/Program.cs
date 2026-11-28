using KyberWeave.Cli;
using KyberWeave.Cli.Rendering;
using Spectre.Console;

ConsoleWidth.EnsureUsable(AnsiConsole.Console);

// The one process-wide read of where the command was run: findings' rooted paths are shown relative to it.
ReportRenderer.DisplayRoot = Environment.CurrentDirectory;

return await CliApplication.Create().RunAsync(args).ConfigureAwait(false);
