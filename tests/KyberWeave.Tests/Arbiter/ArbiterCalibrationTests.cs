using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The labelled set the step-1 thresholds are measured against, and the measurement.
/// The set is checked offline on every run. The measurement makes paid provider calls,
/// so it runs only when <c>KW_ARBITER_CALIBRATE_OUT</c> names an output file.
/// </summary>
public sealed class ArbiterCalibrationTests
{
    private const string OutputVariable = "KW_ARBITER_CALIBRATE_OUT";
    private const string ModelsVariable = "KW_ARBITER_CALIBRATE_MODELS";
    private const string DefaultModels = "jev-1.13.0,jev-1.14.0";
    private const string EndpointVariable = "KW_ARBITER_CALIBRATE_ENDPOINT";
    private const string LimitVariable = "KW_ARBITER_CALIBRATE_LIMIT";
    private const string CasesVariable = "KW_ARBITER_CALIBRATE_CASES";
    private const string DefaultEndpoint = "https://api.typesafe.ai/v1";

    /// <summary>Stops the run past about $0.25 of input at $0.042 per million tokens.</summary>
    private const long MaxInputTokens = 6_000_000;

    /// <summary>Stops the run past this reported spend, for providers that report a cost.</summary>
    private const double MaxReportedCostUsd = 0.30;

    private const int MaxConsecutiveFailures = 3;

    private static string CasesPath([CallerFilePath] string sourcePath = "") =>
        Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "Fixtures", "arbiter-calibration", "cases.jsonl");

    /// <summary>Every case names a shipped step-1 rule, one of its answers, and every fact it reads.</summary>
    [Fact]
    public void Cases_MatchTheShippedRules()
    {
        List<CalibrationCase> cases = LoadCases(CasesPath());

        Assert.NotEmpty(cases);
        Assert.Equal(cases.Count, cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (CalibrationCase item in cases)
        {
            ArbiterRule rule = FindRule(item.Rule);
            Assert.NotNull(rule.Ask);
            Assert.Contains(item.Label, rule.Answers);
            Assert.All(rule.Ask.State.Values, fact => Assert.True(item.Facts.Contains(fact), $"{item.Id} lacks {fact}"));
        }
    }

    /// <summary>
    /// Sends each case on its own through the production request builder and records the
    /// raw answer, so a threshold can be chosen afterwards without another call.
    /// </summary>
    [Fact]
    public async Task Measure_RecordsRawAnswersWhenRequested()
    {
        string? output = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(output))
        {
            Assert.Skip($"Makes paid provider calls; set {OutputVariable} to run it.");
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string endpoint = Environment.GetEnvironmentVariable(EndpointVariable) ?? DefaultEndpoint;
        Uri requestUri = SystemOneContracts.RequestUri(new Uri(endpoint));
        string? key = ArbiterKeyResolver.Resolve(endpoint, CreateStore());
        Assert.False(string.IsNullOrWhiteSpace(key), "No key resolved for the endpoint.");

        string models = Environment.GetEnvironmentVariable(ModelsVariable) ?? DefaultModels;
        List<CalibrationCase> cases = LoadCases(Environment.GetEnvironmentVariable(CasesVariable) ?? CasesPath());
        if (int.TryParse(Environment.GetEnvironmentVariable(LimitVariable), CultureInfo.InvariantCulture, out int limit))
        {
            cases = cases.Take(limit).ToList();
        }

        long inputTokens = 0;
        double reportedCost = 0;
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };
        await using StreamWriter writer = new(output, append: false, new UTF8Encoding(false));

        foreach (string model in models.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int failures = 0;
            foreach (CalibrationCase item in cases)
            {
                Assert.True(inputTokens < MaxInputTokens, $"Stopped at the token cap after {inputTokens} input tokens.");
                Assert.True(reportedCost < MaxReportedCostUsd, $"Stopped at the cost cap after ${reportedCost}.");
                ArbiterRule rule = FindRule(item.Rule);

                // No instructions resolver, because production passes none.
                SystemOneBuiltRequest built = SystemOneContracts.BuildRequest([rule], item.Facts, model, null);
                (HttpStatusCode status, string body) = await SendAsync(client, requestUri, key!, built.BodyJson, cancellationToken);

                Dictionary<string, object?> record = new(StringComparer.Ordinal)
                {
                    ["id"] = item.Id,
                    ["rule"] = item.Rule,
                    ["label"] = item.Label,
                    ["difficulty"] = item.Difficulty,
                    ["requested-model"] = model,
                    ["http"] = (int)status,
                };

                if (status == HttpStatusCode.OK
                    && SystemOneContracts.TryParseResponse(body, out SystemOneParsedResponse? parsed)
                    && parsed is not null)
                {
                    failures = 0;
                    inputTokens += parsed.InputTokens ?? built.TotalRequestTokens;
                    double? cost = ReadReportedCost(body);
                    reportedCost += cost ?? 0;
                    record["served-model"] = parsed.Model;
                    record["input-tokens"] = parsed.InputTokens;
                    record["cost-usd"] = cost;
                    if (parsed.AnswersByRule.TryGetValue(rule.Id, out JsonElement answer))
                    {
                        if (SystemOneContracts.TryReadChoice(answer, out string? choice, out double? confidence))
                        {
                            record["choice"] = choice;
                            record["confidence"] = confidence;
                        }
                        else if (SystemOneContracts.TryReadNoul(answer, out double probability))
                        {
                            record["probability"] = probability;
                        }
                    }
                }
                else
                {
                    failures++;
                    Assert.True(failures < MaxConsecutiveFailures, $"Stopped after {failures} failures; last HTTP status {(int)status}.");
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(record).AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpClient client, Uri requestUri, string key, string body, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is not ((HttpStatusCode)429 or (HttpStatusCode)529) || attempt >= 4)
            {
                return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
            }

            await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
        }
    }

    private static double? ReadReportedCost(string body)
    {
        using JsonDocument json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("usage", out JsonElement usage)
            && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("cost", out JsonElement cost)
            && cost.ValueKind == JsonValueKind.Number
                ? cost.GetDouble()
                : null;
    }

    private static ICredentialStore? CreateStore()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainCredentialStore(new ProcessRunnerCredentialProcessRunner());
        }

        return OperatingSystem.IsLinux()
            ? new SecretServiceCredentialStore(new ProcessRunnerCredentialProcessRunner())
            : null;
    }

    private static ArbiterRule FindRule(string id) =>
        Assert.Single(ArbiterConfig.ProductDefaults.Rules, rule => string.Equals(rule.Id, id, StringComparison.Ordinal));

    private static List<CalibrationCase> LoadCases(string path) =>
        File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseCase)
            .ToList();

    private static CalibrationCase ParseCase(string line)
    {
        using JsonDocument json = JsonDocument.Parse(line);
        JsonElement root = json.RootElement;
        Dictionary<string, ArbiterFact> facts = new(StringComparer.Ordinal);
        foreach (JsonProperty fact in root.GetProperty("facts").EnumerateObject())
        {
            facts[fact.Name] = new ArbiterFact(ToValue(fact.Value), ArbiterFactLabel.Asserted);
        }

        return new CalibrationCase(
            root.GetProperty("id").GetString()!,
            root.GetProperty("rule").GetString()!,
            root.GetProperty("label").GetString()!,
            root.GetProperty("difficulty").GetString()!,
            new ArbiterFactSet(facts));
    }

    private static object? ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray().Select(ToValue).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(item => item.Name, item => ToValue(item.Value), StringComparer.Ordinal),
        JsonValueKind.Number => element.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetString(),
    };

    private sealed record CalibrationCase(string Id, string Rule, string Label, string Difficulty, ArbiterFactSet Facts);
}
