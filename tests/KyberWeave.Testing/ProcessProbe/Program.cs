using System.Globalization;

if (args.Length == 0)
{
    return 2;
}

switch (args[0])
{
    case "echo":
        await Console.Out.WriteAsync(args.Length > 1 ? args[1] : string.Empty).ConfigureAwait(false);
        return 0;

    case "both":
        await Console.Out.WriteAsync("out").ConfigureAwait(false);
        await Console.Error.WriteAsync("err").ConfigureAwait(false);
        return 0;

    case "fail":
        await Console.Error.WriteAsync(args.Length > 2 ? args[2] : string.Empty).ConfigureAwait(false);
        return int.Parse(args[1], CultureInfo.InvariantCulture);

    case "cat":
        await Console.Out.WriteAsync(await Console.In.ReadToEndAsync().ConfigureAwait(false)).ConfigureAwait(false);
        return 0;

    case "read":
        await Console.Out
            .WriteAsync(await File.ReadAllTextAsync(Path.Combine(Environment.CurrentDirectory, args[1])).ConfigureAwait(false))
            .ConfigureAwait(false);
        return 0;

    case "env":
        await Console.Out.WriteAsync(Environment.GetEnvironmentVariable(args[1]) ?? "<unset>").ConfigureAwait(false);
        return 0;

    case "sleep":
        await Task.Delay(int.Parse(args[1], CultureInfo.InvariantCulture)).ConfigureAwait(false);
        return 0;

    default:
        return 2;
}
