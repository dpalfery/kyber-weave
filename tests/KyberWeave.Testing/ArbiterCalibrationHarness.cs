using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Tests;

/// <summary>
/// The pieces of the Arbiter calibration run that need no provider: reading the labelled cases, choosing the models and
/// the case limit from the run's variables, and sending one request with its failures reported rather than thrown.
/// The unit tests drive these offline; the measurement that makes paid calls is in the integration project.
/// </summary>
internal static class ArbiterCalibrationHarness
{
    internal const string ModelsVariable = "KW_ARBITER_CALIBRATE_MODELS";
    internal const string LimitVariable = "KW_ARBITER_CALIBRATE_LIMIT";
    internal const string DefaultModels = "jev-1.13.0,jev-1.14.0";

    /// <summary>The status recorded for a call that got no HTTP response.</summary>
    internal const HttpStatusCode TransportFailure = 0;

    internal static readonly Uri ProbeUri = new("https://calibration.invalid/v1");

    /// <summary>The models to measure: the default when unset, and never an empty list.</summary>
    internal static string[] ParseModels(string? value)
    {
        string[] models = (value ?? DefaultModels).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return models.Length > 0 ? models : throw new ArgumentException($"{ModelsVariable} names no model.", nameof(value));
    }

    /// <summary>All the cases when unset; otherwise the first <paramref name="value"/>, which must be a positive count.</summary>
    internal static List<CalibrationCase> ApplyLimit(List<CalibrationCase> cases, string? value)
    {
        if (value is null)
        {
            return cases;
        }

        return int.TryParse(value, CultureInfo.InvariantCulture, out int limit) && limit > 0
            ? cases.Take(limit).ToList()
            : throw new ArgumentException($"{LimitVariable} must be a positive count.", nameof(value));
    }

    internal static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpClient client, Uri requestUri, string key, string body, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
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
            }
            catch (HttpRequestException exception)
            {
                return (TransportFailure, exception.Message);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                return (TransportFailure, exception.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(1 << attempt), cancellationToken);
        }
    }

    internal static List<CalibrationCase> LoadCases(string path) =>
        File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(ParseCase)
            .ToList();

    internal static CalibrationCase ParseCase(string line)
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

    internal static object? ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => element.EnumerateArray().Select(ToValue).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(item => item.Name, item => ToValue(item.Value), StringComparer.Ordinal),
        JsonValueKind.Number => element.GetDouble().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetString(),
    };

    internal sealed record CalibrationCase(string Id, string Rule, string Label, string Difficulty, ArbiterFactSet Facts);

    internal sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}
