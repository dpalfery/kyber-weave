using System.Globalization;
using KyberDash.Core;
using KyberDash.Core.Exceptions;

namespace KyberDash.Api;

/// <summary>
/// Parses the <c>kyberdash-api</c> command line into <see cref="DashRuntimeSettings"/>.
/// </summary>
/// <remarks>
/// Parsing is deliberately a pure function of <c>args</c> and the environment so it can be tested
/// without binding a socket, and so the process never half-starts on a malformed option.
/// </remarks>
internal static class DashCommandLine
{
    /// <summary>Recognised options, used to build the "did you mean" hint on a typo.</summary>
    private static readonly string[] KnownOptions =
    [
        "web",
        "--port",
        "--otlp-port",
        "--db",
        "--state-dir",
        "--no-open",
        "--open",
        "--view",
        "--otlp-only",
        "--no-otlp",
        "--version",
        "--help",
        "-h",
    ];

    private static readonly string[] ViewPatterns =
    [
        "/",
        "/harness/:harnessId",
        "/run/:runId",
        "/session/:sessionId",
        "/session/:sessionId/turn/:turnIndex",
        "/finding/:findingId",
        "/compare?a=:runId&b=:runId",
        "/quarantine",
        "/problems",
    ];

    /// <summary>Parses <paramref name="args"/>, or throws <see cref="DashValidationException"/> naming the bad option.</summary>
    public static DashRuntimeSettings Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        DashRuntimeSettings settings = new()
        {
            Version = DashVersion.Current,
        };

        int index = 0;
        if (args.Count > 0 && string.Equals(args[0], "web", StringComparison.Ordinal))
            index = 1;

        for (; index < args.Count; index++)
        {
            string arg = args[index];
            switch (arg)
            {
                case "--no-open":
                    settings.OpenBrowser = false;
                    break;

                case "--open":
                    settings.OpenBrowser = true;
                    break;

                case "--otlp-only":
                    settings.OtlpOnly = true;
                    settings.OpenBrowser = false;
                    break;

                case "--no-otlp":
                    settings.OtlpEnabled = false;
                    break;

                case "--port":
                    settings.WebPort = ParsePort(ReadValue(args, ref index, arg), arg);
                    settings.WebPortExplicit = true;
                    break;

                case "--otlp-port":
                    settings.OtlpPort = ParsePort(ReadValue(args, ref index, arg), arg);
                    settings.OtlpPortExplicit = true;
                    break;

                case "--db":
                    settings.DatabasePath = Path.GetFullPath(ReadValue(args, ref index, arg));
                    break;

                case "--state-dir":
                    settings.StateDirectory = Path.GetFullPath(ReadValue(args, ref index, arg));
                    break;

                case "--view":
                    settings.View = ReadValue(args, ref index, arg);
                    break;

                default:
                    throw new DashValidationException(
                        "INVALID_OPTION",
                        $"unknown argument '{arg}'.{NearestOptionHint(arg)} Run `kyberdash-api --help` for usage.");
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.View) && !MatchesViewPath(settings.View))
        {
            throw new DashValidationException(
                "INVALID_VIEW",
                $"unknown view \"{settings.View}\". Valid views: {string.Join(", ", ViewPatterns)}.");
        }

        if (string.IsNullOrEmpty(settings.StateDirectory))
            settings.StateDirectory = DefaultStateDirectory();

        ApplyEnvironmentFallbacks(settings);
        return settings;
    }

    /// <summary>Resolves the state directory used when <c>--state-dir</c> is absent.</summary>
    public static string DefaultStateDirectory()
    {
        string? overrideDir = Environment.GetEnvironmentVariable("KYBERDASH_STATE_DIR")?.Trim();
        if (!string.IsNullOrEmpty(overrideDir))
            return Path.GetFullPath(overrideDir);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".kyberdash");
    }

    /// <summary>Writes the usage text to standard output.</summary>
    public static void WriteHelp(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteLine(
            "kyberdash-api - KyberDash loopback REST/SSE dashboard and OTLP/HTTP collector\n\n"
            + "Usage: kyberdash-api [web] [options]\n\n"
            + "Options:\n"
            + "  --port <n>        Web/REST port (default: KYBER_DASH_PORT, dashboard setting, else 4747; 0 = ephemeral)\n"
            + "  --otlp-port <n>   OTLP collector port (default: KYBER_OTLP_PORT, else 4318; falls back to an\n"
            + "                    ephemeral port when 4318 is busy unless this option is given)\n"
            + "  --no-otlp         Do not start the OTLP collector\n"
            + "  --otlp-only       Start only the OTLP collector\n"
            + "  --db <path>       Canonical SQLite store path\n"
            + "  --state-dir <dir> State directory for the engine lease, server.json, and settings\n"
            + "  --view <path>     Dashboard view to open in the browser\n"
            + "  --open            Open the dashboard in the browser once listening (default)\n"
            + "  --no-open         Do not open a browser\n"
            + "  --version         Print the version and exit\n"
            + "  --help, -h        Print this help and exit");
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new DashValidationException(
                "INVALID_OPTION",
                $"{option} needs a value.{NearestOptionHint(option)} Run `kyberdash-api --help` for usage.");
        }

        index++;
        return args[index];
    }

    private static int ParsePort(string raw, string option)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0
            || !int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 0 or > 65535)
        {
            throw new DashValidationException(
                "INVALID_PORT",
                $"{option} must be a whole number from 0 to 65535, not \"{raw}\".");
        }

        return port;
    }

    /// <summary>
    /// Applies the <c>KYBER_DASH_PORT</c> and <c>KYBER_OTLP_PORT</c> fallbacks. The dashboard
    /// setting is applied later, once the container can read it.
    /// </summary>
    private static void ApplyEnvironmentFallbacks(DashRuntimeSettings settings)
    {
        if (!settings.WebPortExplicit
            && TryReadPortEnvironment("KYBER_DASH_PORT", out int dashPort))
        {
            settings.WebPort = dashPort;
            settings.WebPortExplicit = true;
        }

        if (!settings.OtlpPortExplicit
            && TryReadPortEnvironment("KYBER_OTLP_PORT", out int otlpPort))
        {
            settings.OtlpPort = otlpPort;
            settings.OtlpPortExplicit = true;
        }
    }

    private static bool TryReadPortEnvironment(string name, out int port)
    {
        string? raw = Environment.GetEnvironmentVariable(name)?.Trim();
        port = 0;
        return !string.IsNullOrEmpty(raw)
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is >= 0 and <= 65535;
    }

    private static string NearestOptionHint(string arg)
    {
        if (!arg.StartsWith('-'))
            return string.Empty;

        string? nearest = null;
        int best = int.MaxValue;
        foreach (string candidate in KnownOptions)
        {
            int distance = EditDistance(arg, candidate);
            if (distance < best)
            {
                best = distance;
                nearest = candidate;
            }
        }

        return nearest is not null && best <= 2 ? $" Did you mean '{nearest}'?" : string.Empty;
    }

    private static int EditDistance(string left, string right)
    {
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static bool MatchesViewPath(string view)
    {
        string trimmed = view.Trim();
        int queryIndex = trimmed.IndexOf('?', StringComparison.Ordinal);
        string viewPath = (queryIndex >= 0 ? trimmed[..queryIndex] : trimmed).Trim('/');
        string viewQuery = queryIndex >= 0 ? trimmed[(queryIndex + 1)..] : string.Empty;

        foreach (string pattern in ViewPatterns)
        {
            int patternQueryIndex = pattern.IndexOf('?', StringComparison.Ordinal);
            string patternPath = (patternQueryIndex >= 0 ? pattern[..patternQueryIndex] : pattern).Trim('/');
            string patternQuery = patternQueryIndex >= 0 ? pattern[(patternQueryIndex + 1)..] : string.Empty;

            if (SegmentPatternMatches(viewPath, patternPath)
                && (string.IsNullOrEmpty(patternQuery) || QueryMatches(viewQuery, patternQuery)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SegmentPatternMatches(string viewPath, string patternPath)
    {
        if (patternPath.Length == 0)
            return viewPath.Length == 0;

        string[] viewSegments = viewPath.Split('/', StringSplitOptions.None);
        string[] patternSegments = patternPath.Split('/', StringSplitOptions.None);
        if (viewSegments.Length != patternSegments.Length)
            return false;

        for (int i = 0; i < patternSegments.Length; i++)
        {
            if (patternSegments[i].StartsWith(':'))
            {
                if (viewSegments[i].Length == 0)
                    return false;

                continue;
            }

            if (!string.Equals(viewSegments[i], patternSegments[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool QueryMatches(string viewQuery, string patternQuery)
    {
        Dictionary<string, string> actual = new(StringComparer.Ordinal);
        foreach (string pair in viewQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                continue;

            actual[Uri.UnescapeDataString(pair[..equals])] = Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        foreach (string part in patternQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            string requiredKey = equals >= 0 ? part[..equals] : part;
            if (!actual.TryGetValue(requiredKey, out string? value) || string.IsNullOrEmpty(value))
                return false;
        }

        return true;
    }
}
