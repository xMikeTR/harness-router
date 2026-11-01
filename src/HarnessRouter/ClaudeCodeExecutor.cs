using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HarnessRouter;

/// Sent to the browser as one JSON line each. Unset fields are left out.
public sealed record ExecutorEvent(string Type, string? Text = null, string? Tool = null, string? Session = null, decimal? Cost = null);

public sealed record ExecutorRequest(string Prompt, string Workspace, string Model, string? Session, IReadOnlyCollection<string> AllowedTools);

public interface IExecutor
{
    IAsyncEnumerable<ExecutorEvent> RunAsync(ExecutorRequest request, CancellationToken ct);
}

/// Runs `claude -p` in the workspace with its default permissions: a tool it may not use comes back as a denial,
/// which the UI turns into an approval, and the approved tool is allowed on the resumed run.
public sealed class ClaudeCodeExecutor(string cli = "claude") : IExecutor
{
    public static IReadOnlyList<string> Args(ExecutorRequest r)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--model", r.Model };
        if (r.Session is not null) args.AddRange(["--resume", r.Session]);
        if (r.AllowedTools.Count > 0) args.AddRange(["--allowedTools", string.Join(",", r.AllowedTools)]);
        return args;
    }

    public async IAsyncEnumerable<ExecutorEvent> RunAsync(ExecutorRequest r, [EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new ProcessStartInfo(cli)
        {
            WorkingDirectory = r.Workspace, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in Args(r)) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {cli}");
        using var kill = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        // The prompt goes on stdin: it can never be read as a flag.
        await proc.StandardInput.WriteAsync(r.Prompt);
        proc.StandardInput.Close();

        while (await proc.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            foreach (var e in Parse(line)) yield return e;

        await proc.WaitForExitAsync(CancellationToken.None);
        if (proc.ExitCode != 0 && !ct.IsCancellationRequested)
            yield return new("error", Text: $"claude exited {proc.ExitCode}: {await stderr}");
    }

    public static IEnumerable<ExecutorEvent> Parse(string line)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(line).RootElement; }
        catch (JsonException) { yield break; } // not an event: a stray log line
        switch (S(root, "type"))
        {
            case "assistant" when root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content):
                foreach (var c in content.EnumerateArray())
                {
                    if (S(c, "type") == "text") yield return new("text", Text: S(c, "text"));
                    else if (S(c, "type") == "tool_use") yield return new("tool", Tool: S(c, "name"));
                }
                break;
            case "result":
                if (root.TryGetProperty("permission_denials", out var denials))
                    foreach (var name in denials.EnumerateArray().Select(d => S(d, "tool_name")).Where(n => n is not null).Distinct())
                        yield return new("denied", Tool: name);
                yield return new(root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True ? "error" : "done",
                    Text: root.TryGetProperty("is_error", out var e2) && e2.ValueKind == JsonValueKind.True ? S(root, "result") : null,
                    Session: S(root, "session_id"),
                    Cost: root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null);
                break;
        }
    }

    private static string? S(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
