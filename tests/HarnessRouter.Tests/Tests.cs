using HarnessRouter;
using Xunit;

namespace HarnessRouter.Tests;

public class RoutingTests
{
    private sealed class Fixed(string phase, double conf) : IClassifier
    {
        public int Calls;
        public Task<Classification> ClassifyAsync(string text, CancellationToken ct) { Calls++; return Task.FromResult(new Classification(phase, conf)); }
    }
    private sealed class Down : IClassifier
    {
        public Task<Classification> ClassifyAsync(string text, CancellationToken ct) => throw new HttpRequestException("down");
    }

    [Theory]
    [InlineData("docs", "haiku")]
    [InlineData("plan", "opus")]
    [InlineData("debug", "sonnet")]
    public async Task A_confident_phase_picks_the_model_from_the_policy(string phase, string model)
    {
        var d = Assert.IsType<RouteDecision.Go>(await new Router(new Fixed(phase, .9)).DecideAsync("do it", default));
        Assert.Equal(model, d.Route.Model);
    }

    [Fact]
    public async Task Low_confidence_takes_the_safe_default()
    {
        var d = Assert.IsType<RouteDecision.Go>(await new Router(new Fixed("docs", .5)).DecideAsync("do it", default));
        Assert.Equal(Router.DefaultModel, d.Route.Model);
    }

    [Fact]
    public async Task Laya_being_down_takes_the_safe_default()
    {
        var d = Assert.IsType<RouteDecision.Go>(await new Router(new Down()).DecideAsync("do it", default));
        Assert.Equal(Router.DefaultModel, d.Route.Model);
        Assert.Contains("unavailable", d.Route.Reason);
    }

    [Theory]
    [InlineData("Migrate the policy database to Postgres")]
    [InlineData("Rotate the credentials for payments")]
    [InlineData("Build a new module for exposure management")]
    public async Task Safety_rules_stop_before_laya_is_asked(string text)
    {
        var laya = new Fixed("docs", .99);
        Assert.IsType<RouteDecision.SendToS1>(await new Router(laya).DecideAsync(text, default));
        Assert.Equal(0, laya.Calls);
    }

    [Fact]
    public void Route_override_parses_and_ordinary_text_does_not()
    {
        Assert.Equal(("opus", "fix it"), Router.ParseOverride("/route opus fix it"));
        Assert.Null(Router.ParseOverride("please /route opus fix it"));
        Assert.Null(Router.ParseOverride("/route gpt fix it"));
    }
}

public class LayaClientTests
{
    private sealed class Capture : HttpMessageHandler
    {
        public long? Length;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Length = r.Content!.Headers.ContentLength;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"phase":"docs","confidence":0.9}""", System.Text.Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task The_request_has_a_length_and_the_answer_is_read()
    {
        var h = new Capture();
        var c = await new LayaClient(new HttpClient(h) { BaseAddress = new Uri("http://x/") }).ClassifyAsync("hi", default);
        Assert.NotNull(h.Length);
        Assert.Equal(new Classification("docs", .9), c);
    }
}

public class ClaudeCodeExecutorTests
{
    [Fact]
    public void A_follow_up_resumes_and_carries_approved_tools()
    {
        var args = ClaudeCodeExecutor.Args(new("hi", "/w", "opus", "s1", ["Bash"]));
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--model", "opus", "--resume", "s1", "--allowedTools", "Bash"], args);
    }

    [Fact]
    public async Task Fake_cli_output_becomes_events_with_the_prompt_on_stdin()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var cli = Path.Combine(dir, "fake-claude");
        await File.WriteAllTextAsync(cli, """
            #!/bin/sh
            cat > "$(dirname "$0")/stdin"
            echo '{"type":"system","subtype":"init","session_id":"s1"}'
            echo 'not json'
            echo '{"type":"assistant","message":{"content":[{"type":"text","text":"hello"},{"type":"tool_use","name":"Read"}]}}'
            echo '{"type":"result","is_error":false,"session_id":"s1","total_cost_usd":0.01,"permission_denials":[{"tool_name":"Bash"},{"tool_name":"Bash"}]}'
            """.Replace("            ", ""));
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var events = new List<ExecutorEvent>();
        await foreach (var e in new ClaudeCodeExecutor(cli).RunAsync(new("--evil-flag", dir, "sonnet", null, []), default)) events.Add(e);

        Assert.Equal(["text", "tool", "denied", "done"], events.Select(e => e.Type));
        Assert.Equal("s1", events[^1].Session);
        Assert.Equal(0.01m, events[^1].Cost);
        Assert.Equal("--evil-flag", await File.ReadAllTextAsync(Path.Combine(dir, "stdin")));
    }

    [Fact]
    public async Task A_failing_cli_reports_an_error()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var cli = Path.Combine(dir, "fake-claude");
        await File.WriteAllTextAsync(cli, "#!/bin/sh\ncat >/dev/null\necho boom >&2\nexit 3\n");
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var events = new List<ExecutorEvent>();
        await foreach (var e in new ClaudeCodeExecutor(cli).RunAsync(new("x", dir, "sonnet", null, []), default)) events.Add(e);

        Assert.Contains("boom", Assert.Single(events).Text);
    }
}

public class ChatServiceTests
{
    private sealed class Fixed : IClassifier
    {
        public int Calls;
        public Task<Classification> ClassifyAsync(string text, CancellationToken ct) { Calls++; return Task.FromResult(new Classification("plan", .9)); }
    }
    private sealed class Recording : IExecutor
    {
        public List<ExecutorRequest> Requests = [];
        public async IAsyncEnumerable<ExecutorEvent> RunAsync(ExecutorRequest r, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(r);
            await Task.Yield();
            yield return new("done", Session: "s1");
        }
    }

    [Fact]
    public async Task A_follow_up_is_pinned_routes_once_and_resumes_with_the_approved_tool()
    {
        var laya = new Fixed(); var exec = new Recording();
        var ledger = Path.GetTempFileName();
        var chat = new ChatService(new Router(laya), exec, "/w", ledger);

        string? id = null;
        await foreach (var e in chat.HandleAsync(new("plan it"), default))
            if (e.GetType().GetProperty("conversation")?.GetValue(e) is string c) id = c;
        await foreach (var _ in chat.HandleAsync(new("go on", id, ["Bash"]), default)) { }

        Assert.Equal(1, laya.Calls);
        Assert.Equal("opus", exec.Requests[1].Model);
        Assert.Equal("s1", exec.Requests[1].Session);
        Assert.Contains("Bash", exec.Requests[1].AllowedTools);
        var log = await File.ReadAllTextAsync(ledger);
        Assert.DoesNotContain("plan it", log); // metadata only
        Assert.Contains("\"approved\"", log);
    }

    [Fact]
    public async Task A_stopped_request_never_starts_the_harness_but_route_overrides_it()
    {
        var exec = new Recording();
        var chat = new ChatService(new Router(new Fixed()), exec, "/w", Path.GetTempFileName());
        await foreach (var _ in chat.HandleAsync(new("Migrate production"), default)) { }
        Assert.Empty(exec.Requests);
        await foreach (var _ in chat.HandleAsync(new("/route opus Migrate production"), default)) { }
        Assert.Equal("Migrate production", Assert.Single(exec.Requests).Prompt);
    }
}
