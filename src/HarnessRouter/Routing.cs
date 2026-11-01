using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace HarnessRouter;

public sealed record Classification(string Phase, double Confidence);

public sealed record Route(string Harness, string Model, string Reason, string? Phase = null, double? Confidence = null);

public abstract record RouteDecision
{
    public sealed record Go(Route Route) : RouteDecision;
    /// Needs design or is high risk: S1's job, unless the person overrides with /route.
    public sealed record SendToS1(string Reason) : RouteDecision;
}

public interface IClassifier
{
    Task<Classification> ClassifyAsync(string text, CancellationToken ct);
}

public sealed class LayaClient(HttpClient http) : IClassifier
{
    public async Task<Classification> ClassifyAsync(string text, CancellationToken ct)
    {
        // A sized body, not chunked: the Python service reads by Content-Length.
        using var body = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { text }), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("classify", body, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Classification>(cancellationToken: ct)
            ?? throw new InvalidDataException("empty answer from laya");
    }
}

/// Plain rules, because Laya missed most high-risk requests in testing. They are checked first and fail predictably.
public static partial class SafetyRules
{
    [GeneratedRegex(@"\b(prod|production|credentials?|secrets?|migrat\w*|regulat\w*|retention|delete\s+\w*\s*(records?|data)|new (module|service|line of business|platform|portal)|architecture)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Stop();

    public static string? Check(string text) => Stop().Match(text) is { Success: true } m ? $"mentions \"{m.Value}\"" : null;
}

public sealed class Router(IClassifier classifier, double minConfidence = 0.7)
{
    public const string Harness = "claude-code";
    public const string DefaultModel = "sonnet";

    /// Prices and quality belong in data: this table is the only place a phase meets a model.
    private static readonly Dictionary<string, string> Policy = new()
    {
        ["plan"] = "opus", ["implement"] = "sonnet", ["debug"] = "sonnet",
        ["test"] = "sonnet", ["refactor"] = "sonnet", ["docs"] = "haiku",
    };

    public async Task<RouteDecision> DecideAsync(string text, CancellationToken ct)
    {
        if (SafetyRules.Check(text) is { } stop) return new RouteDecision.SendToS1(stop);
        try
        {
            var c = await classifier.ClassifyAsync(text, ct);
            if (c.Confidence < minConfidence)
                return new RouteDecision.Go(new(Harness, DefaultModel, $"low confidence on {c.Phase}: safe default", c.Phase, c.Confidence));
            return new RouteDecision.Go(Policy.TryGetValue(c.Phase, out var model)
                ? new(Harness, model, $"phase {c.Phase}", c.Phase, c.Confidence)
                : new(Harness, DefaultModel, $"unknown phase {c.Phase}: safe default", c.Phase, c.Confidence));
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or System.Text.Json.JsonException
                                    || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new RouteDecision.Go(new(Harness, DefaultModel, $"laya unavailable ({e.GetType().Name}): safe default"));
        }
    }

    /// "/route opus fix the thing" => (opus, "fix the thing"). Null when the message is not an override.
    public static (string Model, string Text)? ParseOverride(string message)
    {
        var m = Regex.Match(message, @"^/route\s+(opus|sonnet|haiku)\s+(.+)$", RegexOptions.Singleline);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }
}
