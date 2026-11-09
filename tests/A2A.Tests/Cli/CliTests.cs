using System.Text.Json;
using A2A.Cli;
using A2A.Cli.Commands;
using Microsoft.AspNetCore.Builder;
using static A2A.Tests.Cli.CliTestSupport;

namespace A2A.Tests.Cli;

public sealed class CliTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    // ---- card ----

    [Fact]
    public async Task Card_prints_text_with_unsigned_status()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "card");
        Assert.Equal(0, run.Code);
        Assert.Contains("Name:", run.Out);
        Assert.Contains("Scenario Agent", run.Out);
        Assert.Contains("Verification: Unsigned", run.Out);
        Assert.Contains("assurance OriginOnly", run.Out);
        Assert.Equal("", run.Error);
    }

    [Fact]
    public async Task Card_json_has_source_verification_and_card()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "card", "--json");
        Assert.Equal(0, run.Code);
        using var doc = JsonDocument.Parse(run.Out);
        Assert.EndsWith("/.well-known/agent-card.json", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("unsigned", doc.RootElement.GetProperty("verification").GetProperty("status").GetString());
        Assert.Equal("Scenario Agent", doc.RootElement.GetProperty("card").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Require_signature_fails_with_7_for_an_unsigned_card()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "card", "--require-signature");
        Assert.Equal(7, run.Code);
        Assert.Contains("Error:", run.Error);
        Assert.Equal("", run.Out);
    }

    [Fact]
    public async Task Json_error_is_one_line_on_stderr()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "card", "--require-signature", "--json");
        Assert.Equal(7, run.Code);
        var line = Assert.Single(run.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        using var doc = JsonDocument.Parse(line);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(7, error.GetProperty("exitCode").GetInt32());
        Assert.False(string.IsNullOrEmpty(error.GetProperty("message").GetString()));
    }

    // ---- send, stream, subscribe ----

    [Fact]
    public async Task Send_prints_the_result_as_text()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "send", "hello");
        Assert.Equal(0, run.Code);
        Assert.Contains("hi there", run.Out);
    }

    [Fact]
    public async Task Send_json_prints_the_response_object()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "send", "--json", "work please");
        Assert.Equal(0, run.Code);
        using var doc = JsonDocument.Parse(run.Out);
        Assert.Equal("TASK_STATE_COMPLETED", doc.RootElement.GetProperty("task").GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Send_reads_the_text_from_stdin_with_a_dash()
    {
        await using var host = await StartAsync();
        var run = await RunAsync(host, ["send", "-"], stdin: "hello");
        Assert.Equal(0, run.Code);
        Assert.Contains("hi there", run.Out);
    }

    [Fact]
    public async Task Send_failed_task_exits_9()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "send", "fail now");
        Assert.Equal(9, run.Code);
        Assert.Contains("Failed", run.Out);
    }

    [Fact]
    public async Task Send_input_required_exits_10()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "send", "input please");
        Assert.Equal(10, run.Code);
        Assert.Contains("InputRequired", run.Out);
    }

    [Fact]
    public async Task Stream_json_is_ndjson_with_several_events_and_a_final_state()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "stream", "--json", "task go");
        Assert.Equal(0, run.Code);
        var lines = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 4, run.Out);
        foreach (var line in lines) JsonDocument.Parse(line).Dispose();
        Assert.Contains("TASK_STATE_COMPLETED", lines[^1]);
        Assert.Contains(lines, l => l.Contains("artifactUpdate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stream_text_prints_one_line_per_event()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "stream", "task go");
        Assert.Equal(0, run.Code);
        Assert.Contains(run.Out.Split('\n'), l => l.StartsWith("artifact ", StringComparison.Ordinal));
        Assert.Contains("Completed", run.Out);
    }

    [Fact]
    public async Task Stream_input_required_exits_10()
    {
        await using var host = await StartAsync();
        Assert.Equal(10, (await Run(host, "stream", "input please")).Code);
    }

    [Fact]
    public async Task Subscribe_prints_events_until_the_task_ends()
    {
        await using var host = await StartAsync();
        var started = await Run(host, "send", "--no-wait", "--json", "gate subscribe-test");
        Assert.Equal(0, started.Code);
        var id = TaskIdOf(started.Out);

        var writer = new SignalWriter();
        var subscribed = RunAsync(host, ["subscribe", "--", id], output: writer);
        await writer.FirstLine.Task.WaitAsync(Limit);
        ScenarioAgent.Gate("gate subscribe-test").TrySetResult();
        var run = await subscribed;
        Assert.Equal(0, run.Code);
        Assert.Contains("Completed", run.Out);
    }

    // ---- get, list, cancel ----

    [Fact]
    public async Task Get_list_and_cancel_work_on_a_task()
    {
        await using var host = await StartAsync();
        var started = await Run(host, "send", "--no-wait", "--json", "gate cancel-test");
        var id = TaskIdOf(started.Out);

        var got = await Run(host, "get", "--", id);
        Assert.True(got.Code == 0, got.Shown);
        Assert.Contains(id, got.Out);

        var cancelled = await Run(host, "cancel", "--json", "--", id);
        Assert.True(cancelled.Code == 0, cancelled.Shown);
        Assert.Contains("TASK_STATE_CANCELED", cancelled.Out);

        await host.WaitIdleAsync();
        var list = await Run(host, "list", "--status", "Canceled");
        Assert.True(list.Code == 0, list.Shown);
        Assert.Contains(id, list.Out);
        var listJson = await Run(host, "list", "--json", "--page-size", "5");
        Assert.True(listJson.Code == 0, listJson.Shown);
        Assert.Contains(id, listJson.Out);
    }

    [Fact]
    public async Task Get_unknown_task_exits_5()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "get", "no-such-task");
        Assert.Equal(5, run.Code);
        Assert.Contains("Error:", run.Error);
    }

    [Fact]
    public async Task Json_error_for_a_missing_task_names_the_a2a_code()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "cancel", "no-such-task", "--json");
        Assert.Equal(5, run.Code);
        using var doc = JsonDocument.Parse(run.Error.Trim());
        Assert.Equal("TASK_NOT_FOUND", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- usage ----

    [Theory]
    [InlineData("send", "--bogus", "x")]
    [InlineData("send", "--context-id")]
    [InlineData("send", "--history-length", "abc", "x")]
    [InlineData("send", "--history-length", "-1", "x")]
    [InlineData("send")]
    [InlineData("send", "one", "two")]
    [InlineData("get")]
    [InlineData("card", "extra")]
    [InlineData("list", "--status", "bogus")]
    [InlineData("card", "--transport", "carrier-pigeon")]
    [InlineData("card", "--timeout", "0")]
    [InlineData("card", "--json=yes")]
    [InlineData("card", "--trust-jku-host", "")]
    [InlineData("card", "--trust-jku-host", "  ")]
    [InlineData("card", "--client-id", "only-id")]
    [InlineData("card", "--client-secret", "only-secret")]
    [InlineData("nonsense")]
    public async Task Usage_errors_exit_2(params string[] args)
    {
        await using var host = await StartAsync();
        var run = await Run(host, args);
        Assert.Equal(2, run.Code);
        Assert.Equal("", run.Out);
        Assert.Contains("Error:", run.Error);
    }

    [Fact]
    public async Task Missing_agent_url_is_a_usage_error()
    {
        await using var host = await StartAsync();
        var run = await RunAsync(host, ["card"], addAgent: false);
        Assert.Equal(2, run.Code);
        Assert.Contains("A2A_AGENT_URL", run.Error);
    }

    [Fact]
    public async Task Agent_url_with_user_info_is_a_usage_error_and_is_not_echoed()
    {
        await using var host = await StartAsync();
        var run = await RunAsync(host, ["card", "--agent", "https://user:hunter2@agent.example/"], addAgent: false);
        Assert.Equal(2, run.Code);
        Assert.DoesNotContain("hunter2", run.All);
    }

    [Fact]
    public async Task No_arguments_is_a_usage_error()
    {
        await using var host = await StartAsync();
        Assert.Equal(2, (await RunAsync(host, [], addAgent: false)).Code);
    }

    [Fact]
    public async Task Help_and_version_exit_0()
    {
        await using var host = await StartAsync();
        var help = await RunAsync(host, ["--help"], addAgent: false);
        Assert.Equal(0, help.Code);
        Assert.Contains("Exit codes:", help.Out);
        Assert.Contains("download", help.Out);

        var sendHelp = await RunAsync(host, ["send", "--help"], addAgent: false);
        Assert.Equal(0, sendHelp.Code);
        Assert.Contains("--no-wait", sendHelp.Out);

        var version = await RunAsync(host, ["--version"], addAgent: false);
        Assert.Equal(0, version.Code);
        Assert.False(string.IsNullOrWhiteSpace(version.Out));
    }

    [Fact]
    public async Task Double_dash_ends_options()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "send", "--", "--hello-world");
        Assert.Equal(0, run.Code);
        Assert.Contains("--hello-world", run.Out);
    }

    [Fact]
    public async Task Equals_syntax_and_short_agent_option_work()
    {
        await using var host = await StartAsync();
        var run = await RunAsync(host, ["card", "-a", Agent, "--timeout=20"], addAgent: false);
        Assert.Equal(0, run.Code);
    }

    [Theory]
    [InlineData("working", TaskState.Working)]
    [InlineData("INPUT-REQUIRED", TaskState.InputRequired)]
    [InlineData("input_required", TaskState.InputRequired)]
    [InlineData("TASK_STATE_FAILED", TaskState.Failed)]
    public void Status_names_are_read_in_any_case(string text, TaskState expected) =>
        Assert.Equal(expected, TaskCommands.ParseState(text));

    // ---- auth ----

    [Fact]
    public async Task Card_that_demands_auth_without_a_credential_exits_4()
    {
        await using var host = await StartAsync(SecuredCard(SecurityScheme.Bearer()));
        var run = await Run(host, "send", "hello");
        Assert.Equal(4, run.Code);
    }

    [Fact]
    public async Task Card_command_works_even_when_the_card_demands_auth()
    {
        await using var host = await StartAsync(SecuredCard(SecurityScheme.Bearer()));
        Assert.Equal(0, (await Run(host, "card")).Code);
    }

    [Fact]
    public async Task Server_401_exits_4()
    {
        await using var host = await StartAsync(requireAuth: true);
        var run = await Run(host, "send", "hello");
        Assert.Equal(4, run.Code);
    }

    private static Action<WebApplication> Authenticate(List<(string Path, string? Authorization, string? ApiKey)> seen, string expectedAuthorization, string? expectedApiKey = null) =>
        app => app.Use((ctx, next) =>
        {
            var auth = ctx.Request.Headers.Authorization.ToString();
            var key = ctx.Request.Headers["X-Api-Key"].ToString();
            lock (seen) seen.Add((ctx.Request.Path, auth, key));
            if (ctx.Request.Path.StartsWithSegments("/a2a")
                && (expectedApiKey is null ? auth == expectedAuthorization : key == expectedApiKey))
                ctx.Request.Headers["X-Test-User"] = "cli";
            return next(ctx);
        });

    [Fact]
    public async Task Bearer_token_reaches_the_server_and_the_card_fetch_has_none()
    {
        var seen = new List<(string, string?, string?)>();
        await using var host = await StartAsync(SecuredCard(SecurityScheme.Bearer()), requireAuth: true, pipeline: Authenticate(seen, "Bearer tok-123"));
        var run = await RunAsync(host, ["send", "hello", "--token", "tok-123"]);
        Assert.Equal(0, run.Code);
        lock (seen)
        {
            Assert.Contains(seen, s => s.Item1.StartsWith("/a2a", StringComparison.Ordinal) && s.Item2 == "Bearer tok-123");
            Assert.All(seen.Where(s => s.Item1.Contains("agent-card", StringComparison.Ordinal)), s => Assert.Equal("", s.Item2));
        }
    }

    [Fact]
    public async Task Api_key_reaches_the_server()
    {
        var seen = new List<(string, string?, string?)>();
        await using var host = await StartAsync(SecuredCard(SecurityScheme.ApiKey("X-Api-Key")), requireAuth: true, pipeline: Authenticate(seen, "", "key-456"));
        var run = await RunAsync(host, ["send", "hello", "--api-key", "key-456"]);
        Assert.Equal(0, run.Code);
        lock (seen) Assert.Contains(seen, s => s.Item1.StartsWith("/a2a", StringComparison.Ordinal) && s.Item3 == "key-456");
    }

    [Fact]
    public async Task Environment_variables_are_used_and_options_beat_them()
    {
        var seen = new List<(string, string?, string?)>();
        await using var host = await StartAsync(SecuredCard(SecurityScheme.Bearer()), requireAuth: true, pipeline: Authenticate(seen, "Bearer from-option"));
        var env = new Dictionary<string, string?> { ["A2A_AGENT_URL"] = Agent, ["A2A_TOKEN"] = "from-env" };

        var viaEnv = await RunAsync(host, ["send", "hello"], env, addAgent: false);
        Assert.Equal(4, viaEnv.Code); // the env token is sent, and this server rejects it

        var viaOption = await RunAsync(host, ["send", "hello", "--token", "from-option"], env, addAgent: false);
        Assert.Equal(0, viaOption.Code);
        lock (seen)
        {
            Assert.Contains(seen, s => s.Item2 == "Bearer from-env");
            Assert.Contains(seen, s => s.Item2 == "Bearer from-option");
        }
    }

    [Fact]
    public async Task A_credential_for_a_card_that_asks_for_none_is_not_sent_and_says_so()
    {
        var seen = new List<(string, string?, string?)>();
        await using var host = await StartAsync(pipeline: Authenticate(seen, "never"));
        var run = await RunAsync(host, ["send", "hello", "--token", "tok-unused"]);
        Assert.Equal(0, run.Code);
        Assert.Contains("asks for no credentials", run.Error);
        lock (seen) Assert.All(seen, s => Assert.Equal("", s.Item2));
        Assert.DoesNotContain("tok-unused", run.All);
    }

    [Fact]
    public async Task Secrets_never_appear_in_output_even_when_the_agent_echoes_them_or_the_server_fails()
    {
        const string Secret = "s3cr3t-T0KEN-value";
        var seen = new List<(string, string?, string?)>();
        await using var host = await StartAsync(SecuredCard(SecurityScheme.Bearer()), requireAuth: true, pipeline: Authenticate(seen, "Bearer " + Secret));

        // The agent echoes the message text, which is the secret.
        var echo = await RunAsync(host, ["send", Secret, "--token", Secret]);
        Assert.Equal(0, echo.Code);
        Assert.Contains("***", echo.Out);
        Assert.DoesNotContain(Secret, echo.All);

        var echoJson = await RunAsync(host, ["send", Secret, "--token", Secret, "--json"]);
        Assert.DoesNotContain(Secret, echoJson.All);

        var echoStream = await RunAsync(host, ["stream", Secret, "--token", Secret]);
        Assert.DoesNotContain(Secret, echoStream.All);

        // The server fails (agent crash), and the call fails with a wrong token.
        var failed = await RunAsync(host, ["send", "fail " + Secret, "--token", Secret]);
        Assert.DoesNotContain(Secret, failed.All);
        var wrong = await RunAsync(host, ["send", "hello", "--token", Secret + "-wrong"]);
        Assert.NotEqual(0, wrong.Code);
        Assert.DoesNotContain(Secret, wrong.All);

        // The same through environment variables, and for the api key and client secret.
        var env = new Dictionary<string, string?> { ["A2A_TOKEN"] = Secret, ["A2A_API_KEY"] = "api-" + Secret, ["A2A_CLIENT_ID"] = "client-1", ["A2A_CLIENT_SECRET"] = "cs-" + Secret };
        var viaEnv = await RunAsync(host, ["send", "x " + Secret + " api-" + Secret + " cs-" + Secret], env);
        Assert.Equal(0, viaEnv.Code);
        Assert.DoesNotContain(Secret, viaEnv.All);
    }

    [Fact]
    public void Redactor_replaces_plain_and_json_escaped_secrets()
    {
        var redactor = new SecretRedactor();
        redactor.Add("a+b\"c");
        Assert.Equal("x *** y", redactor.Redact("x a+b\"c y"));
        Assert.Equal("x *** y", redactor.Redact("x a\\u002Bb\\u0022c y"));
        Assert.Equal("nothing", redactor.Redact("nothing"));
    }

    [Fact]
    public void Error_text_loses_url_queries_and_user_info()
    {
        var text = ErrorText.StripUrlSecrets("failed GET https://u:p@store.example/f/a.bin?X-Sig=abc#frag now; see http://x/y?z");
        Assert.Equal("failed GET https://store.example/f/a.bin now; see http://x/y", text);
    }

    // ---- agent text ----

    [Fact]
    public async Task Control_and_bidi_characters_from_the_agent_are_stripped_in_text_mode()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "send", "hi \u001b[31mred\u001b[0m \u202Eevil\u202C end");
        Assert.Equal(0, run.Code);
        Assert.False(run.Out.Contains('\u001b'), run.Shown);
        Assert.False(run.Out.Contains('\u202E'), run.Shown);
        Assert.False(run.Out.Contains('\u202C'), run.Shown);
        Assert.Contains("red", run.Out);
    }

    [Fact]
    public async Task Json_mode_escapes_control_characters()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "send", "--json", "hi \u001b[31mred");
        Assert.False(run.Out.Contains('\u001b'), run.Shown);
        Assert.Contains("\\u001B", run.Shown);
    }

    // ---- cancellation and timeouts ----

    [Fact]
    public async Task Ctrl_c_exits_130()
    {
        await using var host = await StartAsync();
        using var cts = new CancellationTokenSource();
        var writer = new SignalWriter();
        var running = RunAsync(host, ["stream", "slow forever"], output: writer, cancellationToken: cts.Token);
        await writer.FirstLine.Task.WaitAsync(Limit);
        cts.Cancel();
        var run = await running;
        Assert.Equal(130, run.Code);
    }

    [Fact]
    public async Task Stream_timeout_exits_3()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "stream", "slow forever", "--timeout", "0.5");
        Assert.Equal(3, run.Code);
    }

    [Fact]
    public async Task Send_timeout_exits_3()
    {
        await using var host = await StartAsync();
        var run = await Run(host, "send", "slow forever", "--timeout", "0.5", "--retries", "0");
        Assert.Equal(3, run.Code);
    }

    [Fact]
    public async Task Unreachable_agent_exits_3()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var env = new CliEnvironment(stdout, stderr, new StringReader(""), _ => null)
        {
            Handler = new ThrowingHandler(),
        };
        var code = await CliApp.RunAsync(["card", "--agent", "http://localhost:1/"], env, CancellationToken.None);
        Assert.Equal(3, code);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused", null, null);
    }

    // ---- file names ----

    [Theory]
    [InlineData("report.txt", "report.txt")]
    [InlineData("../../evil.txt", "evil.txt")]
    [InlineData("..\\..\\evil.txt", "evil.txt")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\win.ini", "win.ini")]
    [InlineData(".hidden", "hidden")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("tab\there\u0007bell\u202Ebidi.txt", "tabherebellbidi.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("name. . ", "name")]
    public void File_names_are_made_safe(string input, string expected) =>
        Assert.Equal(expected, FileNames.Sanitize(input, "fallback"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("...")]
    public void Unusable_names_fall_back(string? input) => Assert.Equal("art-3", FileNames.Sanitize(input, "art-3"));

    [Fact]
    public void Fallback_is_sanitized_too_and_long_names_are_capped()
    {
        Assert.Equal("evil", FileNames.Sanitize(null, "../evil"));
        Assert.Equal(FileNames.MaxLength, FileNames.Sanitize(new string('a', 500) + ".txt", "x").Length);
    }

    [Fact]
    public void Duplicate_names_get_numbered_before_the_extension()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a.txt", FileNames.Unique("a.txt", used));
        Assert.Equal("a-2.txt", FileNames.Unique("a.txt", used));
        Assert.Equal("A-3.TXT", FileNames.Unique("A.TXT", used));
    }
}
