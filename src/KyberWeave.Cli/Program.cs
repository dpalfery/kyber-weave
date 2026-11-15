using KyberWeave.Cli;
using KyberWeave.Cli.Rendering;
using Spectre.Console;

ConsoleWidth.EnsureUsable(AnsiConsole.Console);

return CliApplication.Create().Run(args);
