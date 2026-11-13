using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using A2A.Client;
using A2A.Client.V0_3Compat;
using static A2A.V0_3Compat.Tests.ClientData;

namespace A2A.V0_3Compat.Tests;

/// <summary>
///     <see cref="A2AClientOptions.StreamKeepAliveTimeout" /> on the v0.3 transports: after a keep-alive comment, a stream
///     that goes silent without closing fails as a dropped connection instead of waiting for ever.
/// </summary>
public class StreamKeepAliveTimeoutTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);

    private static HttpResponseMessage KeepAliveThenSilence(string data)
    {
        var content = new StreamContent(new HangingStream(Encoding.UTF8.GetBytes($": ka\n\ndata: {data}\n\n")));
        content.Headers.ContentType = new("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static async Task<(List<StreamResponse> Events, Exception? Error)> Read(IAsyncEnumerable<StreamResponse> stream)
    {
        var events = new List<StreamResponse>();
        try
        {
            await foreach (var e in stream) events.Add(e);
            return (events, null);
        }
        catch (Exception ex)
        {
            return (events, ex);
        }
    }

    [Fact]
    public async Task JsonRpc_stream_fails_as_a_dropped_connection_after_silence()
    {
        var event1 = FakeV03Server.RpcEvent(JsonNode.Parse(StatusUpdateJson("working", final: false))!).ToJsonString();
        var server = new FakeV03Server(_ => KeepAliveThenSilence(event1));
        var transport = new V03JsonRpcTransport(server.Client(), new Uri("http://agent.test/rpc"),
            new A2AClientOptions { MaxRetries = 0, StreamKeepAliveTimeout = Short });

        var (events, error) = await Read(transport.SendStreamingMessageAsync(Send("hi"))).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(events);
        Assert.IsType<TimeoutException>(Assert.IsType<IOException>(error).InnerException);
    }

    [Fact]
    public async Task HttpJson_stream_fails_as_a_dropped_connection_after_silence()
    {
        var server = new FakeV03Server(_ => KeepAliveThenSilence(RestStatusUpdateJson("TASK_STATE_WORKING", false)));
        var transport = new V03HttpJsonTransport(server.Client(), new Uri("http://agent.test/api"),
            new A2AClientOptions { MaxRetries = 0, StreamKeepAliveTimeout = Short });

        var (events, error) = await Read(transport.SendStreamingMessageAsync(Send("hi"))).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(events);
        Assert.IsType<TimeoutException>(Assert.IsType<IOException>(error).InnerException);
    }
}
