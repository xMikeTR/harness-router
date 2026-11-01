using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace HarnessRouter;

public sealed record ChatRequest(string Message, string? ConversationId = null, string[]? Approve = null);

/// A conversation is pinned to the route chosen for its first message; follow-ups resume the same session.
public sealed class Conversation(string id, Route route)
{
    public string Id { get; } = id;
    public Route Route { get; } = route;
    public string? Session { get; set; }
    public HashSet<string> AllowedTools { get; } = [];
    public SemaphoreSlim Turn { get; } = new(1, 1);
}

public sealed class ChatService(Router router, IExecutor executor, string workspace, string ledgerPath)
{
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();
    private static readonly SemaphoreSlim LedgerLock = new(1, 1);

    public async IAsyncEnumerable<object> HandleAsync(ChatRequest req, [EnumeratorCancellation] CancellationToken ct)
    {
        Conversation? conv;
        var text = req.Message;
        var overridden = false;
        if (req.ConversationId is not null)
        {
            if (!_conversations.TryGetValue(req.ConversationId, out conv))
            {
                yield return new { type = "error", text = "unknown conversation" };
                yield break;
            }
        }
        else
        {
            RouteDecision decision;
            if (Router.ParseOverride(text) is { } o)
            {
                (var model, text) = o;
                decision = new RouteDecision.Go(new(Router.Harness, model, "manual /route"));
                overridden = true;
            }
            else decision = await router.DecideAsync(text, ct);

            if (decision is RouteDecision.SendToS1 s1)
            {
                await LogAsync(new { kind = "stopped", reason = s1.Reason }, ct);
                yield return new { type = "stopped", text = $"This {s1.Reason}, so it belongs in S1. To run it here anyway, start with /route sonnet (or opus, haiku)." };
                yield break;
            }
            conv = new Conversation(Guid.NewGuid().ToString("N"), ((RouteDecision.Go)decision).Route);
            _conversations[conv.Id] = conv;
            await LogAsync(new { kind = "routed", conversation = conv.Id, conv.Route, overridden }, ct);
            yield return new { type = "route", conversation = conv.Id, harness = conv.Route.Harness, model = conv.Route.Model, reason = conv.Route.Reason, phase = conv.Route.Phase, confidence = conv.Route.Confidence };
        }

        foreach (var tool in req.Approve ?? [])
        {
            conv.AllowedTools.Add(tool);
            await LogAsync(new { kind = "approved", conversation = conv.Id, tool }, ct);
        }

        await conv.Turn.WaitAsync(ct);
        try
        {
            var denied = new List<string>();
            decimal? cost = null;
            var status = "no_result";
            var request = new ExecutorRequest(text, workspace, conv.Route.Model, conv.Session, conv.AllowedTools.ToList());
            await foreach (var e in executor.RunAsync(request, ct))
            {
                if (e.Session is not null) conv.Session = e.Session;
                if (e.Type == "denied") denied.Add(e.Tool!);
                if (e.Type is "done" or "error") { status = e.Type; cost = e.Cost ?? cost; }
                yield return e;
            }
            await LogAsync(new { kind = "turn", conversation = conv.Id, conv.Route.Model, status, cost, denied }, ct);
        }
        finally { conv.Turn.Release(); }
    }

    /// Metadata only: never the prompt or the agent's output.
    private async Task LogAsync(object row, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(new { ts = DateTimeOffset.UtcNow, row = (object)row }) + "\n";
        await LedgerLock.WaitAsync(ct);
        try { await File.AppendAllTextAsync(ledgerPath, line, ct); }
        finally { LedgerLock.Release(); }
    }
}
