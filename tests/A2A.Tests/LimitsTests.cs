using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace A2A.Tests;

public class LimitsTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private const string OneMessage = """{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"hi"}]}}""";

    private static HttpRequestMessage SendRequest(string binding, string paramsJson, string user = "alice")
    {
        var rpc = binding == ProtocolBindings.JsonRpc;
        var req = new HttpRequestMessage(HttpMethod.Post, rpc ? "/a2a" : "/a2a/message:send")
        {
            Content = new StringContent(
                rpc ? $$"""{"jsonrpc":"2.0","id":1,"method":"{{A2AProtocol.Methods.SendMessage}}","params":{{paramsJson}}}""" : paramsJson,
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add("X-Test-User", user);
        return req;
    }

    private static HttpRequestMessage GetTaskRequest(string binding, string user = "alice")
    {
        var rpc = binding == ProtocolBindings.JsonRpc;
        var req = rpc
            ? new HttpRequestMessage(HttpMethod.Post, "/a2a")
            {
                Content = new StringContent($$$"""{"jsonrpc":"2.0","id":1,"method":"{{{A2AProtocol.Methods.GetTask}}}","params":{"id":"nope"}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpRequestMessage(HttpMethod.Get, "/a2a/tasks/nope");
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add("X-Test-User", user);
        return req;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Send(TestAgentHost host, HttpRequestMessage req)
    {
        using var client = host.Http();
        using var resp = await client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string ErrorText(JsonElement body) => body.GetProperty("error").GetRawText();

    private static string Deep(int levels) => string.Concat(Enumerable.Repeat("{\"a\":", levels)) + "1" + new string('}', levels);

    // ---- defaults ----

    [Fact]
    public void Defaults_match_the_spec()
    {
        var l = new A2ALimits();
        Assert.Equal(10 * 1024 * 1024, l.MaxRequestBodyBytes);
        Assert.Equal(100, l.MaxPartsPerMessage);
        Assert.Equal(8 * 1024 * 1024, l.MaxRawPartBytes);
        Assert.Equal(32, l.MaxJsonDepth);
        Assert.Equal(100, l.MaxHistoryLengthReturned);
        Assert.Equal(100, l.MaxListPageSize);
        Assert.Equal(10, l.MaxConcurrentStreamsPerTask);
        Assert.Equal(50, l.MaxConcurrentStreamsPerPrincipal);
        Assert.Equal(4 * 1024 * 1024, l.MaxEventBytes);
        Assert.Equal(TimeSpan.FromHours(1), l.MaxStreamDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), l.StreamReauthInterval);
        Assert.Equal(10, l.MaxPushConfigsPerTask);
        Assert.NotNull(new A2AServerOptions().Limits);
        Assert.True(new A2AServerOptions().EnableRateLimiting);
    }

    // ---- body size ----

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Body_over_limit_returns_413_payload_too_large(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxRequestBodyBytes = 1024);
        var big = $$$"""{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"{{{new string('x', 2048)}}}"}]}}""";
        var (status, body) = await Send(host, SendRequest(binding, big));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Contains("PAYLOAD_TOO_LARGE", ErrorText(body));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Body_under_limit_is_accepted(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxRequestBodyBytes = 1024);
        var (status, _) = await Send(host, SendRequest(binding, OneMessage));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Body_over_limit_is_rejected_without_reading_the_rest()
    {
        // The guard stream never ends and throws if it is read again once it has handed out more than the limit.
        const int limit = 1000;
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxRequestBodyBytes = limit);
        var endpoint = ActivatorUtilities.CreateInstance<JsonRpcEndpoint>(host.App.Services);
        foreach (var chunk in new[] { 1, 100, 999, 1000, 4096 })
        {
            var guard = new EndlessGuardStream(limit, chunk);
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = host.App.Services };
            http.Request.Method = "POST";
            http.Request.ContentType = "application/json";
            http.Request.Body = guard;
            http.Response.Body = new MemoryStream();
            await endpoint.HandleAsync(http);

            Assert.Equal(413, http.Response.StatusCode);
            http.Response.Body.Position = 0;
            var error = JsonDocument.Parse(http.Response.Body).RootElement.GetProperty("error");
            Assert.Equal($"Request body exceeds the limit of {limit} bytes", error.GetProperty("message").GetString());
            Assert.Contains("PAYLOAD_TOO_LARGE", error.GetRawText());
            Assert.InRange(guard.Delivered, limit + 1, limit + chunk);
        }
    }

    [Fact]
    public async Task Declared_content_length_over_limit_is_rejected_before_reading()
    {
        const int limit = 1000;
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxRequestBodyBytes = limit);
        var endpoint = ActivatorUtilities.CreateInstance<JsonRpcEndpoint>(host.App.Services);
        var guard = new EndlessGuardStream(-1, 1); // any read throws
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = host.App.Services };
        http.Request.Method = "POST";
        http.Request.ContentLength = limit + 1;
        http.Request.Body = guard;
        http.Response.Body = new MemoryStream();
        await endpoint.HandleAsync(http);
        Assert.Equal(413, http.Response.StatusCode);
        Assert.Equal(0, guard.Delivered);
    }

    [Fact]
    public async Task Declared_content_length_does_not_decide_the_up_front_allocation()
    {
        // A client declares a body at the 10 MB limit, sends 10 bytes, then stalls. Only bytes that arrive may grow the buffer.
        const long limit = 10 * 1024 * 1024;
        var pool = new RecordingPool();
        var stream = new StallingStream(Encoding.UTF8.GetBytes("{\"a\":123}"));
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.ContentLength = limit;
        http.Request.Body = stream;

        var reading = HttpPlumbing.ReadBodyAsync(http.Request, limit, pool);
        await stream.Stalled.WaitAsync(TimeSpan.FromSeconds(10));        // the second read is waiting for bytes that never come
        Assert.Equal([HttpPlumbing.MaxInitialBodyBuffer], pool.Requested);
        Assert.All(pool.Rented, a => Assert.True(a <= HttpPlumbing.MaxInitialBodyBuffer, $"rented {a} bytes"));

        stream.End();
        using (var body = await reading) Assert.Equal("{\"a\":123}", Encoding.UTF8.GetString(body.Memory.Span));
        Assert.Equal(pool.Rented.Count, pool.Returned);

        // A body that does arrive grows the buffer by doubling, from the same small start.
        var big = new byte[300 * 1024];
        big.AsSpan().Fill((byte)' ');
        var grown = new RecordingPool();
        var http2 = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http2.Request.ContentLength = big.Length;
        http2.Request.Body = new MemoryStream(big);
        using (var body = await HttpPlumbing.ReadBodyAsync(http2.Request, limit, grown)) Assert.Equal(big.Length, body.Length);
        Assert.Equal(HttpPlumbing.MaxInitialBodyBuffer, grown.Requested[0]);
        Assert.Equal(grown.Rented.Count, grown.Returned);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(null, "{\"a\":1}")]
    [InlineData(7L, "{\"a\":1}")]
    public async Task Unbounded_body_limit_does_not_overflow(long? contentLength, string body)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.ContentLength = contentLength;
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var read = await HttpPlumbing.ReadBodyAsync(http.Request, long.MaxValue);
        Assert.Equal(body, Encoding.UTF8.GetString(read.Memory.Span));

        // and end to end
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxRequestBodyBytes = long.MaxValue);
        var (status, _) = await Send(host, SendRequest(ProtocolBindings.HttpJson, OneMessage));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>An <see cref="System.Buffers.ArrayPool{T}"/> over the shared pool that records what is rented and returned.</summary>
    private sealed class RecordingPool : System.Buffers.ArrayPool<byte>
    {
        public List<int> Requested { get; } = [];
        public List<int> Rented { get; } = [];
        public int Returned { get; private set; }

        public override byte[] Rent(int minimumLength)
        {
            Requested.Add(minimumLength);
            var array = Shared.Rent(minimumLength);
            Rented.Add(array.Length);
            return array;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Returned++;
            Shared.Return(array, clearArray);
        }
    }

    /// <summary>Returns its bytes on the first read, then waits on the next read until <see cref="End"/>.</summary>
    private sealed class StallingStream(byte[] first) : Stream
    {
        private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sent;
        public Task Stalled => _stalled.Task;
        public void End() => _end.TrySetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                first.CopyTo(buffer);
                return first.Length;
            }
            _stalled.TrySetResult();
            await _end.Task.WaitAsync(cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Json_kept_by_a_handler_outlives_the_pooled_body(string binding)
    {
        // A handler keeps the JsonElement of request A. Request B (same size, so it reuses the pooled array) must not change it,
        // and neither may returning (and clearing) A's buffer.
        var kept = new System.Collections.Concurrent.ConcurrentQueue<JsonElement>();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2AServer().AddExtensionOperation("x.keep", "urn:test:x-keep",
            (_, parameters, _) => { kept.Enqueue(parameters); return ValueTask.FromResult(A2A.Json.A2AJson.EmptyObject); }, null, true));

        HttpRequestMessage Keep(char fill)
        {
            var json = "{\"v\":\"" + new string(fill, 4000) + "\",\"n\":{\"m\":[1,2,3]}}";
            var req = binding == ProtocolBindings.JsonRpc
                ? new HttpRequestMessage(HttpMethod.Post, "/a2a") { Content = new StringContent($$"""{"jsonrpc":"2.0","id":1,"method":"x.keep","params":{{json}}}""", Encoding.UTF8, "application/json") }
                : new HttpRequestMessage(HttpMethod.Post, "/a2a/ext/x.keep") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
            req.Headers.Add("X-Test-User", "alice");
            return req;
        }

        Assert.Equal(HttpStatusCode.OK, (await Send(host, Keep('A'))).Status);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await Send(host, Keep('B'))).Status);

        Assert.True(kept.TryDequeue(out var first));
        Assert.Equal(new string('A', 4000), first.GetProperty("v").GetString());
        Assert.Equal("[1,2,3]", first.GetProperty("n").GetProperty("m").GetRawText());
        Assert.All(kept, e => Assert.Equal(new string('B', 4000), e.GetProperty("v").GetString()));
    }

    /// <summary>An endless body of spaces, <c>chunk</c> bytes per read, that throws if read again after passing <c>limit</c>.</summary>
    private sealed class EndlessGuardStream(long limit, int chunk) : Stream
    {
        public long Delivered { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (Delivered > limit) throw new InvalidOperationException($"read past the limit: {Delivered} bytes already delivered");
            var n = Math.Min(buffer.Length, chunk);
            buffer[..n].Fill((byte)' ');
            Delivered += n;
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer.AsSpan(offset, count)));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- parts ----

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Too_many_parts_rejected(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var message = new Message
        {
            MessageId = "m1", Role = Role.User,
            Parts = Enumerable.Range(0, 101).Select(i => (Part)Part.FromText($"p{i}")).ToList(),
        };
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).SendMessageAsync(message));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        Assert.Contains("100", ex.Message);
    }

    [Fact]
    public void Exactly_the_part_limit_is_accepted()
    {
        var message = new Message { MessageId = "m1", Role = Role.User, Parts = Enumerable.Range(0, 100).Select(i => (Part)Part.FromText("p")).ToList() };
        RequestLimitValidator.Validate(message, new A2ALimits());
    }

    [Fact]
    public void Raw_part_over_limit_rejected()
    {
        var limits = new A2ALimits { MaxRawPartBytes = 16 };
        var message = new Message { MessageId = "m1", Role = Role.User, Parts = [Part.FromBytes(new byte[32], "application/octet-stream")] };
        var ex = Assert.Throws<A2AException>(() => RequestLimitValidator.Validate(message, limits));
        Assert.Equal(413, ex.HttpStatus);
        RequestLimitValidator.Validate(new Message { MessageId = "m2", Role = Role.User, Parts = [Part.FromBytes(new byte[16], "application/octet-stream")] }, limits);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Raw_part_over_limit_returns_413_on_the_wire(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxRawPartBytes = 16);
        var raw = Convert.ToBase64String(new byte[32]);
        var json = $$$"""{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"raw":"{{{raw}}}"}]}}""";
        var (status, body) = await Send(host, SendRequest(binding, json));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Contains("PAYLOAD_TOO_LARGE", ErrorText(body));
    }

    // ---- JSON shape ----

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Duplicate_json_property_rejected(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        const string dup = """{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a"}]},"message":{"messageId":"m2","role":"ROLE_USER","parts":[{"text":"b"}]}}""";
        var (status, body) = await Send(host, SendRequest(binding, dup));
        AssertInvalidParams(binding, status, body);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Json_deeper_than_32_rejected(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        // 40 sits between the request limit (32) and the serializer ceiling (64); 100 is beyond both. Same answer for both.
        foreach (var levels in new[] { 40, 100 })
        {
            var json = $$$"""{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a"}],"metadata":{{{Deep(levels)}}}}}""";
            var (status, body) = await Send(host, SendRequest(binding, json));
            AssertInvalidParams(binding, status, body);
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Request_at_the_maximum_depth_is_stored_and_served_back(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        // JSON-RPC wraps params two levels deep (root, params, message, metadata), so 29 nested objects reach depth 32 exactly.
        var json = $$$"""{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"task go"}],"metadata":{{{Deep(29)}}}}}""";
        var (status, body) = await Send(host, SendRequest(binding, json));
        Assert.Equal(HttpStatusCode.OK, status);
        var task = binding == ProtocolBindings.JsonRpc ? body.GetProperty("result").GetProperty("task") : body.GetProperty("task");
        var id = task.GetProperty("id").GetString()!;

        foreach (var b in new[] { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson })
        {
            var fetched = await host.Client(b).GetTaskAsync(id);
            Assert.Contains(fetched.History!, m => m.Role == Role.User && m.Metadata is { Count: > 0 });
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Json_within_depth_limit_is_accepted(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var json = $$$"""{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a"}],"metadata":{{{Deep(10)}}}}}""";
        var (status, _) = await Send(host, SendRequest(binding, json));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    public static TheoryData<string, string> DuplicateBodies()
    {
        var data = new TheoryData<string, string>();
        foreach (var binding in new[] { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson })
        {
            data.Add(binding, """{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a"}]},"message":{"messageId":"m2","role":"ROLE_USER","parts":[{"text":"b"}]}}""");
            data.Add(binding, """{"message":{"messageId":"m1","messageId":"m2","role":"ROLE_USER","parts":[{"text":"a"}]}}""");
            data.Add(binding, """{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a","text":"b"}]}}""");
            data.Add(binding, """{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a"}],"metadata":{"x":[[{"y":{"z":1,"z":2}}]]}}}""");
            data.Add(binding, """{"message":{"messageId":"m1","\u006dessageId":"m2","role":"ROLE_USER","parts":[{"text":"a"}]}}""");
            data.Add(binding, "{\"message\":{\"messageId\":\"m1\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"a\"}],\"metadata\":{"
                + string.Join(",", Enumerable.Range(0, 100).Select(i => $"\"k{i}\":{i}")) + ",\"k42\":0}}}"); // a large object
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(DuplicateBodies))]
    public async Task Duplicate_keys_rejected_at_any_depth(string binding, string json)
    {
        await using var host = await TestAgentHost.StartAsync();
        var (status, body) = await Send(host, SendRequest(binding, json));
        AssertInvalidParams(binding, status, body);
        Assert.StartsWith("Invalid request body: ", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Same_names_in_other_objects_or_other_case_are_not_duplicates(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        foreach (var json in new[]
        {
            """{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"a"},{"text":"b"}],"metadata":{"messageId":{"messageId":1}}}}""",
            """{"message":{"messageId":"m2","role":"ROLE_USER","parts":[{"text":"a"}],"metadata":{"k":1,"K":2}}}""",
        })
        {
            var (status, _) = await Send(host, SendRequest(binding, json));
            Assert.Equal(HttpStatusCode.OK, status);
        }
    }

    // Raw JSON-RPC bodies: the error code, message and echoed id for each kind of bad body.
    public static TheoryData<string, int, string, string> RpcBodies() => new()
    {
        { "", -32700, "Invalid JSON payload", "null" },
        { "{", -32700, "Invalid JSON payload", "null" },
        { """{"jsonrpc":"2.0","id":1,"method":"GetTask","params":{"id":"x"},}""", -32700, "Invalid JSON payload", "null" },
        { """{"jsonrpc":"2.0","id":1,"method":"GetTask","params":{"id":"x"}} x""", -32700, "Invalid JSON payload", "null" },
        { "\uFEFF" + """{"jsonrpc":"2.0","id":1,"method":"GetTask","params":{"id":"x"}}""", -32700, "Invalid JSON payload", "null" },
        { """{"jsonrpc":"2.0","id":1,"id":1,"method":"GetTask","params":{"id":"x"} """, -32700, "Invalid JSON payload", "null" }, // duplicate, then malformed
        { """{"jsonrpc":"2.0","id":1,"method":"GetTask","params":""" + Deep(1100) + "}", -32700, "Invalid JSON payload", "null" }, // past 1024 is malformed
        { """{"jsonrpc":"2.0","id":1,"method":"GetTask","params":""" + Deep(40) + "}", -32602,
            "Invalid request body: The maximum configured depth of 32 has been exceeded. Cannot read next JSON object. LineNumber: 0 | BytePositionInLine: 207.", "null" },
        { """[{"jsonrpc":"2.0","id":1,"method":"GetTask","params":{"id":"x"}}]""", -32600, "Batch requests are not supported", "null" },
        { """{"jsonrpc":2,"id":7,"method":"GetTask","params":{"id":"x"}}""", -32700, "Invalid JSON payload", "7" },
        { """{"jsonrpc":"2.0","id":"s","method":5,"params":{"id":"x"}}""", -32700, "Invalid JSON payload", "\"s\"" },
        { """{"jsonrpc":null,"id":7,"method":"GetTask","params":{"id":"x"}}""", -32600, "Request payload validation error", "7" },
        { """{"jsonrpc":"2.0","id":{"a":1},"method":"GetTask","params":{"id":"x"}}""", -32600, "id must be a string, number, or null", "{\"a\":1}" },
        { """{"jsonrpc":"2.0","id":1,"method":"Nope","params":[1]}""", -32601, "Method 'Nope' not found", "1" },
        { """{"jsonrpc":"2.0","id":1,"method":"GetTask","params":[1]}""", -32602, "params must be an object", "1" },
        { "  \n{\"jsonrpc\":\"2.0\",\n\"id\":1,\n\"method\":\"GetTask\",\n\"params\":  {\n \"id\": 5}}", -32602,
            "Invalid params: The JSON value could not be converted to System.String. Path: $.id | LineNumber: 1 | BytePositionInLine: 8.", "1" },
        { """{"jsonrpc":"2.0","id":1,"method":"SendMessage"}""", -32602,
            "Invalid params: JSON deserialization for type 'A2A.SendMessageRequest' was missing required properties including: 'message'.", "1" },
        { """{"jsonrpc":"2.0","\u0069d":"esc","\u006dethod":"GetTask","params":{"id":"x"}}""", -32001, "The specified task ID does not exist or is not accessible", "\"esc\"" },
    };

    [Theory]
    [MemberData(nameof(RpcBodies))]
    public async Task Malformed_vs_invalid_error_codes_unchanged(string json, int code, string message, string id)
    {
        await using var host = await TestAgentHost.StartAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/a2a") { Content = new StringContent(json, new UTF8Encoding(false), "application/json") };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add("X-Test-User", "alice");
        var (status, body) = await Send(host, req);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(message, body.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(id, body.GetProperty("id").GetRawText());
    }

    // HTTP+JSON: malformed and invalid bodies are both InvalidParams (400); the message names the problem.
    public static TheoryData<string, string> RestBodies() => new()
    {
        { "{", "Invalid request body: Expected depth to be zero at the end of the JSON payload. There is an open JSON object or array that should be closed. LineNumber: 0 | BytePositionInLine: 1." },
        { "[1]", "Invalid request body: The JSON value could not be converted to A2A.SendMessageRequest. Path: $ | LineNumber: 0 | BytePositionInLine: 1." },
        { "null", "A request body is required" },
        { "{\"message\":" + Deep(40) + "}", "Invalid request body: The maximum configured depth of 32 has been exceeded. Cannot read next JSON object. LineNumber: 0 | BytePositionInLine: 166." },
        { "\n  {\"message\":{\"messageId\":5}}", "Invalid request body: The JSON value could not be converted to A2A.Message. Path: $.message.messageId | LineNumber: 0 | BytePositionInLine: 25." },
    };

    [Theory]
    [MemberData(nameof(RestBodies))]
    public async Task Rest_malformed_and_invalid_bodies_keep_their_messages(string json, string message)
    {
        await using var host = await TestAgentHost.StartAsync();
        var (status, body) = await Send(host, SendRequest(ProtocolBindings.HttpJson, json));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(message, body.GetProperty("error").GetProperty("message").GetString());
    }

    private static void AssertInvalidParams(string binding, HttpStatusCode status, JsonElement body)
    {
        if (binding == ProtocolBindings.JsonRpc)
        {
            // JSON-RPC errors travel in a 200 response.
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal((int)A2AErrorCode.InvalidParams, body.GetProperty("error").GetProperty("code").GetInt32());
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal(400, body.GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public void Net8_duplicate_checker_detects_duplicates()
    {
        static bool Check(string json) => HttpPlumbing.ContainsDuplicateProperties(Encoding.UTF8.GetBytes(json), 32);

        Assert.True(Check("{\"a\":1,\"a\":2}"));
        Assert.True(Check("{\"x\":{\"a\":1,\"a\":2}}"));
        Assert.True(Check("{\"a\":1,\"\\u0061\":2}"));          // escaped spelling of the same name
        Assert.True(Check("[{\"b\":[{\"c\":1,\"c\":2}]}]"));
        Assert.False(Check("{\"a\":1,\"b\":{\"a\":2}}"));        // same name at another level
        Assert.False(Check("[{\"a\":1},{\"a\":2}]"));            // same name in sibling objects
        Assert.False(Check("{\"a\":1,\"A\":2}"));                // names are case sensitive
        Assert.False(Check("{}"));
    }

    [Fact]
    public void Single_pass_check_agrees_with_the_duplicate_checker()
    {
        static string Big(int n, string extra = "") =>
            "{" + string.Join(",", Enumerable.Range(0, n).Select(i => $"\"k{i}\":{{\"k{i}\":[{i}]}}")) + extra + "}";
        var inputs = new[]
        {
            "{\"a\":1,\"a\":2}", "{\"x\":{\"a\":1,\"a\":2}}", "{\"a\":1,\"\\u0061\":2}", "[{\"b\":[{\"c\":1,\"c\":2}]}]",
            "{\"a\":1,\"b\":{\"a\":2}}", "[{\"a\":1},{\"a\":2}]", "{\"a\":1,\"A\":2}", "{}", "[]", "1", "\"s\"",
            "{\"a\":{\"b\":1},\"b\":2}", "{\"a\":{\"x\":1},\"a\":2}", "{\"a\":[{\"a\":1,\"b\":2}],\"b\":{\"a\":[]},\"c\":3}",
            "{\"\\u0062\":1,\"\\u0063\":{\"\\u0062\":2},\"b\":3}",
            Big(10), Big(16), Big(17), Big(100), Big(100, ",\"k5\":0"), Big(100, ",\"\\u006b99\":0"), Big(20, ",\"x\":{\"k1\":1}"),
            "[" + Big(40) + "," + Big(40) + "]", "{\"o\":" + Big(30) + ",\"o2\":" + Big(30, ",\"k29\":1") + "}",
        };
        foreach (var json in inputs)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            var duplicate = HttpPlumbing.ContainsDuplicateProperties(bytes, 32);
            if (duplicate)
            {
                var ex = Assert.Throws<A2AException>(() => HttpPlumbing.CheckDepthAndDuplicates(bytes, 32));
                Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
            }
            else
            {
                var root = HttpPlumbing.CheckDepthAndDuplicates(Encoding.UTF8.GetBytes("  \n" + json + " \r\n"), 32);
                Assert.Equal(json, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("  \n" + json + " \r\n")[root]));
            }
        }
    }

    [Fact]
    public void Net8_duplicate_checker_enforces_depth()
    {
        Assert.ThrowsAny<JsonException>(() => HttpPlumbing.ContainsDuplicateProperties(Encoding.UTF8.GetBytes(Deep(40)), 32));
        Assert.False(HttpPlumbing.ContainsDuplicateProperties(Encoding.UTF8.GetBytes(Deep(10)), 32));
    }

    // ---- history cap ----

    [Fact]
    public async Task History_capped_without_history_length()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.Limits.MaxHistoryLengthReturned = 2);
        var store = host.App.Services.GetRequiredService<TaskStore>();
        var now = DateTimeOffset.UtcNow;
        var task = new AgentTask
        {
            Id = "t-history", ContextId = "c-history",
            Status = new AgentTaskStatus { State = TaskState.Completed },
            History = Enumerable.Range(0, 4).Select(i => Message.FromUser($"m{i}")).ToList(),
        };
        await store.GetOrCreateContextAsync(new ContextRecord("", "alice", "c-history", now));
        await store.CreateAsync(new TaskRecord("", "alice", task, 0, now, now));

        var client = host.Client(ProtocolBindings.HttpJson);
        var capped = await client.GetTaskAsync("t-history");
        Assert.Equal(["m2", "m3"], capped.History!.Select(m => m.GetText()));
        Assert.Single((await client.GetTaskAsync("t-history", 1)).History!);
        Assert.Equal(2, (await client.GetTaskAsync("t-history", 50)).History!.Count);   // a larger request is capped too
        Assert.Null((await client.GetTaskAsync("t-history", 0)).History);
    }

    // ---- rate limiting ----

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Rate_limit_returns_429_with_retry_after(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2ARateLimiting(o => { o.TokenLimit = 2; o.TokensPerPeriod = 1; o.ReplenishmentPeriod = TimeSpan.FromHours(1); }));
        for (var i = 0; i < 2; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, GetTaskRequest(binding))).Status);

        using var client = host.Http();
        using var resp = await client.SendAsync(GetTaskRequest(binding));
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.True(resp.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero, "Retry-After header is missing or not positive");
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("RATE_LIMITED", text);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Rate_limit_partitions_by_principal(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2ARateLimiting(o => { o.TokenLimit = 2; o.TokensPerPeriod = 1; o.ReplenishmentPeriod = TimeSpan.FromHours(1); }));
        for (var i = 0; i < 2; i++) await Send(host, GetTaskRequest(binding, "alice"));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(host, GetTaskRequest(binding, "alice"))).Status);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, GetTaskRequest(binding, "bob"))).Status);
    }

    [Fact]
    public async Task Rate_limit_cannot_be_bypassed_by_varying_the_tenant()
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddA2ARateLimiting(o => { o.TokenLimit = 2; o.TokensPerPeriod = 1; o.ReplenishmentPeriod = TimeSpan.FromHours(1); }));
        HttpRequestMessage Req(string tenant, string user)
        {
            var r = new HttpRequestMessage(HttpMethod.Get, $"/a2a/{tenant}/tasks/nope");
            r.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
            r.Headers.Add("X-Test-User", user);
            return r;
        }
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, Req("t1", "alice"))).Status);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, Req("t2", "alice"))).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(host, Req("t3", "alice"))).Status);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, Req("t1", "bob"))).Status);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Host_supplied_a2a_policy_replaces_the_default(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 418; // default policy would answer 429 and allow 20 requests
            o.AddPolicy("a2a", _ => RateLimitPartition.GetFixedWindowLimiter("all", __ =>
                new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
        }));
        Assert.NotEqual((HttpStatusCode)418, (await Send(host, GetTaskRequest(binding))).Status);
        using var client = host.Http();
        using var resp = await client.SendAsync(GetTaskRequest(binding));
        Assert.Equal((HttpStatusCode)418, resp.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Rate_limit_can_be_disabled(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(o => o.EnableRateLimiting = false, useA2A: false,
            services: s => s.AddA2ARateLimiting(o => { o.TokenLimit = 1; o.TokensPerPeriod = 1; o.ReplenishmentPeriod = TimeSpan.FromHours(1); }));
        for (var i = 0; i < 5; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Send(host, GetTaskRequest(binding))).Status);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Handler_rate_limit_error_carries_retry_after_on_the_wire(string binding)
    {
        // Closes the Task 2 gap: the 429 raised inside the handler reaches the client with its Retry-After header.
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<TaskStore, RateLimitedTaskStore>());
        using var client = host.Http();
        using var resp = await client.SendAsync(GetTaskRequest(binding));
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), resp.Headers.RetryAfter?.Delta);
    }

    // ---- startup check for the rate limiter middleware ----

    [Fact]
    public async Task Startup_fails_without_the_rate_limiter_middleware()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestAgentHost.StartAsync(useA2A: false));
        Assert.Equal("Call app.UseA2A() before app.UseAuthorization(), or set A2AServerOptions.EnableRateLimiting = false.", ex.Message);
    }

    [Fact]
    public async Task Startup_succeeds_without_the_middleware_when_rate_limiting_is_disabled()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.EnableRateLimiting = false, useA2A: false);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Startup_style_hosting_checks_the_rate_limiter_after_start(bool useA2A, bool expectStopped)
    {
        using var host = new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(s =>
            {
                s.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
                s.AddAuthorization();
                s.AddRouting();
                s.AddA2AServer(o => o.AgentCard = TestAgentHost.DefaultCard()).AddAgentExecutor<ScenarioAgent>();
                s.AddA2AServerAspNetCore();
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                if (useA2A) app.UseA2A();
                app.UseAuthorization();
                app.UseEndpoints(e => e.MapA2A("/a2a").RequireAuthorization());
            })).Build();
        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.Equal(expectStopped, lifetime.ApplicationStopping.IsCancellationRequested);
        await host.StopAsync();
    }
}
