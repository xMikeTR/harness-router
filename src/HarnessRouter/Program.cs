using System.Text.Json;
using System.Text.Json.Serialization;
using HarnessRouter;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(int.Parse(Environment.GetEnvironmentVariable("ROUTER_PORT") ?? "5180")));
var app = builder.Build();

var workspace = Path.GetFullPath(Environment.GetEnvironmentVariable("ROUTER_WORKSPACE") ?? Directory.GetCurrentDirectory());
var ledger = Environment.GetEnvironmentVariable("ROUTER_LEDGER") ?? Path.Combine(Directory.GetCurrentDirectory(), "router-ledger.jsonl");
var laya = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("ROUTER_LAYA_URL") ?? "http://127.0.0.1:8099/"), Timeout = TimeSpan.FromSeconds(15) };
var chat = new ChatService(new Router(new LayaClient(laya)), new ClaudeCodeExecutor(Environment.GetEnvironmentVariable("ROUTER_CLAUDE") ?? "claude"), workspace, ledger);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

// This endpoint starts an agent: a web page must not be able to reach it through the browser (DNS rebinding, cross-site POST).
app.Use(async (ctx, next) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (ctx.Request.Host.Host is not ("localhost" or "127.0.0.1")
        || (origin.Length > 0 && origin != $"http://{ctx.Request.Host}"))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/chat", async (ChatRequest req, HttpResponse res, CancellationToken ct) =>
{
    res.ContentType = "application/x-ndjson";
    await foreach (var e in chat.HandleAsync(req, ct))
    {
        await res.WriteAsync(JsonSerializer.Serialize(e, e.GetType(), json) + "\n", ct);
        await res.Body.FlushAsync(ct);
    }
});

app.Logger.LogInformation("workspace: {Workspace}", workspace);
app.Run();
