using System.Net;
using A2A.Server;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Tests;

/// <summary>
/// A push config of another protocol version (v0.3 compat) is sent the body its encoder writes, and fails for good when no
/// encoder for that version is registered: it never gets a v1.0 body it did not ask for.
/// </summary>
public sealed class PushPayloadEncoderTests
{
    private static readonly PushDelivery Delivery = new(new TaskKey("", "alice", "t1"), "c1", 3, "d1");

    private sealed class Encoder(byte[]? body) : IPushPayloadEncoder
    {
        public string ProtocolVersion => "0.3";
        public string ContentType => "application/json";

        public ValueTask<ReadOnlyMemory<byte>?> EncodeAsync(PushDelivery delivery, ReadOnlyMemory<byte> eventBody, CancellationToken cancellationToken) =>
            ValueTask.FromResult(body is null ? (ReadOnlyMemory<byte>?)null : body);
    }

    private static PushWebhookSender Sender(RecordingHandler hooks, IPushPayloadEncoder? encoder) =>
        new(new HttpMessageInvoker(hooks), new PushNotificationOptions { AllowInsecureUrls = true, AllowPrivateNetworks = true },
            NullLogger.Instance, encoder: () => encoder);

    private static TaskPushNotificationConfig Config(string? version) =>
        new() { Id = "c1", TaskId = "t1", Url = "http://hooks.test/x", ProtocolVersion = version };

    private static readonly byte[] EventBody = """{"statusUpdate":{"taskId":"t1"}}"""u8.ToArray();

    [Fact]
    public async Task A_v03_config_gets_the_encoders_body_and_headers()
    {
        var hooks = new RecordingHandler();
        var result = await Sender(hooks, new Encoder("""{"kind":"task"}"""u8.ToArray()))
            .SendAsync(Config("0.3"), Delivery, 1, EventBody, null, default, CancellationToken.None);

        Assert.Equal(PushSendOutcome.Sent, result.Outcome);
        var (request, body) = Assert.Single(hooks.Calls);
        Assert.Equal("""{"kind":"task"}""", body);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("0.3", Assert.Single(request.Headers.GetValues(A2AProtocol.Headers.Version)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_v03_config_without_its_encoder_or_task_fails_for_good_and_sends_nothing(bool encoderWithoutTask)
    {
        var hooks = new RecordingHandler();
        var result = await Sender(hooks, encoderWithoutTask ? new Encoder(null) : null)
            .SendAsync(Config("0.3"), Delivery, 1, EventBody, null, default, CancellationToken.None);

        Assert.Equal(PushSendOutcome.Permanent, result.Outcome);
        Assert.Equal(encoderWithoutTask ? "TaskNotFound" : "UnsupportedVersion", result.ErrorType);
        Assert.Empty(hooks.Calls);
    }

    [Theory]
    [InlineData(null, "1.0")]
    [InlineData("1.0", "1.0")]
    public async Task A_v1_config_is_sent_the_stored_event_whatever_encoder_is_registered(string? version, string header)
    {
        var hooks = new RecordingHandler();
        await Sender(hooks, new Encoder("""{"kind":"task"}"""u8.ToArray()))
            .SendAsync(Config(version), Delivery, 1, EventBody, null, default, CancellationToken.None);

        var (request, body) = Assert.Single(hooks.Calls);
        Assert.Equal(System.Text.Encoding.UTF8.GetString(EventBody), body);
        Assert.Equal(A2AProtocol.MediaType, request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(header, Assert.Single(request.Headers.GetValues(A2AProtocol.Headers.Version)));
    }
}
