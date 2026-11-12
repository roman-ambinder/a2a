using System.Net;
using System.Text;
using A2A.Client;

namespace A2A.V0_3Compat.Tests;

/// <summary>Shared pieces: the resolver seam, the loss reporter and the version check.</summary>
public class SharedTests
{
    private sealed class CardHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private static A2ACardResolver Resolver(string json, Action<A2ACardResolverOptions>? configure = null, bool normalize = true)
    {
        var options = new A2ACardResolverOptions { DefaultCacheDuration = TimeSpan.Zero };
        if (normalize) options.CardNormalizer = V03CardCodec.NormalizeForResolver;
        configure?.Invoke(options);
        return new A2ACardResolver(new HttpClient(new CardHandler(json)), options);
    }

    [Fact]
    public async Task Resolver_with_the_normalizer_reads_a_v03_only_card()
    {
        var resolved = await Resolver(Golden.Text("agent-card.json")).ResolveAsync(new Uri("https://agent.example/"));
        Assert.Equal("Golden Agent", resolved.Card.Name);
        Assert.All(resolved.Card.SupportedInterfaces, i => Assert.Equal("0.3", i.ProtocolVersion));
        Assert.Equal(A2A.Signing.AgentCardVerificationStatus.Unsigned, resolved.Verification.Status);
        Assert.Contains("protocolVersion", resolved.RawJson); // the raw text is what the agent sent
    }

    [Fact]
    public async Task Resolver_without_the_normalizer_does_not_change_behavior()
    {
        // The v1.0 reader refuses a v0.3 card, as it always did, and names the compat factory.
        var ex = await Assert.ThrowsAsync<A2AException>(() => Resolver(Golden.Text("agent-card.json"), normalize: false).ResolveAsync(new Uri("https://agent.example/")));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Contains("V03CompatClientFactory", ex.Message, StringComparison.Ordinal); // and says where to go
    }

    [Fact]
    public async Task A_signed_v03_card_is_refused_when_signatures_are_required()
    {
        var resolver = Resolver(Golden.Text("agent-card.json"), o => o.Trust.RequireSignature = true);
        await Assert.ThrowsAsync<AgentCardVerificationException>(() => resolver.ResolveAsync(new Uri("https://agent.example/")));
    }

    [Fact]
    public async Task A_malformed_v03_card_becomes_an_invalid_response_error()
    {
        var card = Golden.Object("agent-card.json");
        card["securitySchemes"]!["key"]!["type"] = "magic";
        var ex = await Assert.ThrowsAsync<A2AException>(() => Resolver(card.ToJsonString()).ResolveAsync(new Uri("https://agent.example/")));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public async Task A_v02_card_is_read_through_the_normalizer()
    {
        var card = Golden.Object("agent-card.json");
        card["protocolVersion"] = "0.2.6";
        var resolved = await Resolver(card.ToJsonString()).ResolveAsync(new Uri("https://agent.example/"));
        Assert.All(resolved.Card.SupportedInterfaces, i => Assert.Equal(ProtocolBindings.JsonRpc, i.ProtocolBinding));
    }

    [Fact]
    public async Task A_v01_card_is_refused()
    {
        var card = Golden.Object("agent-card.json");
        card["protocolVersion"] = "0.1.0";
        await Assert.ThrowsAsync<A2AException>(() => Resolver(card.ToJsonString()).ResolveAsync(new Uri("https://agent.example/")));
    }

    [Fact]
    public void Resolver_options_clone_is_shallow_and_keeps_the_normalizer_separate()
    {
        var original = new A2ACardResolverOptions();
        var copy = original.Clone();
        copy.CardNormalizer = c => c;
        Assert.Null(original.CardNormalizer);
        Assert.Same(original.Trust, copy.Trust);
    }

    // ---- reporter ----

    [Fact]
    public void Requests_and_dropped_fields_are_counted_with_their_tags()
    {
        using var recorder = new MeterRecorder();
        V03LossReporter.Request("JSONRPC", "message/send");
        V03LossReporter.Drop("x.y");
        Assert.Contains(("JSONRPC", "message/send"), recorder.Requests());
        Assert.Contains("x.y", recorder.DroppedFields());
    }

    private sealed class WarningLog : Microsoft.Extensions.Logging.ILogger
    {
        public List<(int Id, Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((eventId.Id, logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Legacy_traffic_is_logged_at_Warning_once_per_operation_field_and_agent()
    {
        // Spec A.2: warn when legacy objects are received. Unique names, since the reporter remembers what it logged.
        var log = new WarningLog();
        var id = Guid.NewGuid().ToString("N");

        V03LossReporter.Request("JSONRPC", "op-" + id, log);
        V03LossReporter.Request("JSONRPC", "op-" + id, log);
        V03LossReporter.Request("HTTP+JSON", "op-" + id, log);
        V03LossReporter.Drop("field-" + id, log);
        V03LossReporter.Drop("field-" + id, log);
        V03LossReporter.LegacyAgent("Old agent", "https://old.example/" + id, log);
        V03LossReporter.LegacyAgent("Old agent", "https://old.example/" + id, log);

        Assert.All(log.Entries, e => Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, e.Level));
        Assert.Equal([1602, 1602, 1600, 1603], log.Entries.Select(e => e.Id));
        Assert.Contains("op-" + id, log.Entries[0].Message, StringComparison.Ordinal);
        Assert.Contains("HTTP+JSON", log.Entries[1].Message, StringComparison.Ordinal);
        Assert.Contains("field-" + id, log.Entries[2].Message, StringComparison.Ordinal);
        Assert.Contains("https://old.example/" + id, log.Entries[3].Message, StringComparison.Ordinal);
    }

    // ---- version check ----

    [Fact]
    public void Same_versions_pass_and_different_versions_name_the_package()
    {
        var self = typeof(V03Compat).Assembly;
        V03Compat.RequireSameVersion(self, typeof(AgentCard).Assembly, typeof(V03Compat).Assembly);
        var ex = Assert.Throws<InvalidOperationException>(() => V03Compat.RequireSameVersion(self, typeof(object).Assembly));
        Assert.Contains("A2A.V0_3Compat", ex.Message);
        Assert.Contains("same version", ex.Message);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1", true)]
    [InlineData("1.2", true)]
    [InlineData("2.0", true)]
    [InlineData("0.3", false)]
    [InlineData("0.3.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("junk", false)]
    public void Version_header_veto(string? header, bool veto) => Assert.Equal(veto, V03Compat.IsV1OrLater(header));
}
