using System.Net;
using System.Text;
using A2A.Client.Push;
using A2A.Push;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

public class WebhookSignatureTests
{
    private const string Whsec = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";

    private static byte[] Secret(string credentials = Whsec)
    {
        Assert.True(WebhookSignature.TryParseSecret(credentials, out var secret));
        return secret;
    }

    private static string NewSecret() => WebhookSignature.SecretPrefix + Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 7)).ToArray());

    [Fact]
    public void Sign_matches_standard_webhooks_vector()
    {
        var signature = WebhookSignature.Sign(Secret(), "msg_p5jXN8AQM9LWM0D4loKWxJek", 1614265330, Encoding.UTF8.GetBytes("{\"test\": 2432232314}"));
        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", signature);
    }

    [Theory]
    [InlineData("whsec_")]
    [InlineData("whsec_c2hvcnQ=")] // 5 bytes
    [InlineData("whsec_AAAAAAAAAAAAAAAAAAA")] // 15 bytes of zero-bit padding: not valid base64 length
    [InlineData("MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw")] // no prefix
    [InlineData("whsec_not base64!!")]
    [InlineData("")]
    public void Weak_or_malformed_secrets_are_rejected(string credentials) => Assert.False(WebhookSignature.TryParseSecret(credentials, out _));

    [Fact]
    public void A_16_byte_secret_is_the_shortest_accepted()
    {
        Assert.True(WebhookSignature.TryParseSecret("whsec_" + Convert.ToBase64String(new byte[16]), out var ok));
        Assert.Equal(16, ok.Length);
        Assert.False(WebhookSignature.TryParseSecret("whsec_" + Convert.ToBase64String(new byte[15]), out _));
    }

    private static (Dictionary<string, string> Headers, byte[] Body) Signed(byte[] secret, string id, DateTimeOffset at, string body = "{\"a\":1}")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var ts = at.ToUnixTimeSeconds();
        return (new Dictionary<string, string>
        {
            [WebhookSignature.IdHeader] = id,
            [WebhookSignature.TimestampHeader] = ts.ToString(),
            [WebhookSignature.SignatureHeader] = WebhookSignature.Sign(secret, id, ts, bytes),
        }, bytes);
    }

    [Fact]
    public void Replay_is_duplicate_and_stale_is_rejected()
    {
        var clock = new FakeTimeProvider();
        var receiver = new PushNotificationReceiver(clock);
        var secret = Secret();
        var (headers, body) = Signed(secret, "msg_1", clock.GetUtcNow());

        Assert.Equal(PushValidationResult.Valid, receiver.VerifySignature(headers, body, secret));
        Assert.Equal(PushValidationResult.Duplicate, receiver.VerifySignature(headers, body, secret)); // a replay

        // Too old and too far in the future are stale, and a stale delivery does not burn its id.
        var (old, oldBody) = Signed(secret, "msg_2", clock.GetUtcNow() - TimeSpan.FromMinutes(6));
        Assert.Equal(PushValidationResult.Stale, receiver.VerifySignature(old, oldBody, secret));
        var (future, futureBody) = Signed(secret, "msg_3", clock.GetUtcNow() + TimeSpan.FromMinutes(6));
        Assert.Equal(PushValidationResult.Stale, receiver.VerifySignature(future, futureBody, secret));
        var (inside, insideBody) = Signed(secret, "msg_2", clock.GetUtcNow() - TimeSpan.FromMinutes(4));
        Assert.Equal(PushValidationResult.Valid, receiver.VerifySignature(inside, insideBody, secret));

        // A retry is re-signed with a new timestamp but keeps its id: still a duplicate.
        var (retry, retryBody) = Signed(secret, "msg_1", clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        Assert.Equal(PushValidationResult.Duplicate, receiver.VerifySignature(retry, retryBody, secret));

        // The tolerance can be changed; the old delivery was captured, so it is stale however it is replayed later.
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(PushValidationResult.Stale, receiver.VerifySignature(headers, body, secret));
        Assert.Equal(PushValidationResult.Valid, receiver.VerifySignature(headers, body, secret, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Tampered_body_is_invalid_signature()
    {
        var receiver = new PushNotificationReceiver(new FakeTimeProvider());
        var secret = Secret();
        var (headers, body) = Signed(secret, "msg_1", new FakeTimeProvider().GetUtcNow());
        body[2] ^= 1;
        Assert.Equal(PushValidationResult.InvalidSignature, receiver.VerifySignature(headers, body, secret));

        // Wrong key, wrong id, wrong timestamp, missing or malformed headers.
        var (good, goodBody) = Signed(secret, "msg_2", new FakeTimeProvider().GetUtcNow());
        Assert.Equal(PushValidationResult.InvalidSignature, receiver.VerifySignature(good, goodBody, Secret(NewSecret())));
        Assert.Equal(PushValidationResult.InvalidSignature, receiver.VerifySignature(new Dictionary<string, string>(good) { [WebhookSignature.IdHeader] = "msg_3" }, goodBody, secret));
        Assert.Equal(PushValidationResult.InvalidSignature, receiver.VerifySignature(new Dictionary<string, string>(good) { [WebhookSignature.TimestampHeader] = "1" }, goodBody, secret));
        Assert.Equal(PushValidationResult.InvalidSignature, receiver.VerifySignature(new Dictionary<string, string>(good) { [WebhookSignature.TimestampHeader] = "soon" }, goodBody, secret));
        Assert.Equal(PushValidationResult.InvalidSignature, receiver.VerifySignature(new Dictionary<string, string>(good) { [WebhookSignature.SignatureHeader] = "v1,AAAA" }, goodBody, secret));
        Assert.Equal(PushValidationResult.InvalidSignature, receiver.VerifySignature(new Dictionary<string, string>(), goodBody, secret));
        Assert.Equal(PushValidationResult.InvalidSignature, receiver.VerifySignature(good, goodBody, ReadOnlySpan<byte>.Empty));
        // A rejected delivery must not burn its id.
        Assert.Equal(PushValidationResult.Valid, receiver.VerifySignature(good, goodBody, secret));
    }

    [Fact]
    public void Several_signatures_are_accepted_when_one_matches_and_other_versions_are_skipped()
    {
        var clock = new FakeTimeProvider();
        var receiver = new PushNotificationReceiver(clock);
        var secret = Secret();
        var (headers, body) = Signed(secret, "msg_1", clock.GetUtcNow());
        var other = WebhookSignature.Sign(Secret(NewSecret()), "msg_1", long.Parse(headers[WebhookSignature.TimestampHeader]), body);
        headers[WebhookSignature.SignatureHeader] = $"v2,AAAA {other} {headers[WebhookSignature.SignatureHeader]}"; // rotation: old and new key
        Assert.Equal(PushValidationResult.Valid, receiver.VerifySignature(headers, body, secret));
        Assert.Equal(PushValidationResult.InvalidSignature, receiver.VerifySignature(new Dictionary<string, string>(headers)
            { [WebhookSignature.IdHeader] = "msg_9", [WebhookSignature.SignatureHeader] = "v2," + headers[WebhookSignature.SignatureHeader][3..] }, body, secret));
    }

    [Fact]
    public void Header_names_are_case_insensitive_and_both_checks_work_on_one_delivery()
    {
        var clock = new FakeTimeProvider();
        var receiver = new PushNotificationReceiver(clock);
        var secret = Secret();
        var (headers, body) = Signed(secret, "msg_1", clock.GetUtcNow());
        var shouty = headers.ToDictionary(h => h.Key.ToUpperInvariant(), h => h.Value);
        shouty[A2AProtocol.Headers.DeliveryId] = "msg_1";
        shouty[A2AProtocol.Headers.NotificationToken] = "tok";
        Assert.Equal(PushValidationResult.Valid, receiver.Validate(shouty, "tok"));
        Assert.Equal(PushValidationResult.Valid, receiver.VerifySignature(shouty, body, secret));
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Signed_delivery_verifies(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var whsec = NewSecret();
        var task = (await host.Client(binding).SendMessageAsync(Message.FromUser("task"), new SendMessageConfiguration
        {
            TaskPushNotificationConfig = new TaskPushNotificationConfig
            {
                Url = $"http://hooks.test/{binding}", Token = "secret-token",
                Authentication = new AuthenticationInfo { Scheme = WebhookSignature.Scheme, Credentials = whsec },
            },
        })).RequireTask();
        await host.WaitIdleAsync();

        var calls = host.Webhooks.Calls.Where(c => c.Request.RequestUri!.AbsolutePath == $"/{binding}").ToList();
        Assert.True(calls.Count >= 2);
        var receiver = new PushNotificationReceiver();
        var secret = Secret(whsec);
        foreach (var (request, body) in calls)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            Assert.Equal(request.Headers.GetValues(A2AProtocol.Headers.DeliveryId).Single(), headers[WebhookSignature.IdHeader]);
            Assert.Equal(PushValidationResult.Valid, receiver.VerifySignature(headers, Encoding.UTF8.GetBytes(body), secret));
            Assert.Equal(PushValidationResult.Duplicate, receiver.VerifySignature(headers, Encoding.UTF8.GetBytes(body), secret));
            // The P0 headers are unchanged, and the scheme is not an Authorization header.
            Assert.Equal("secret-token", request.Headers.GetValues(A2AProtocol.Headers.NotificationToken).Single());
            Assert.Equal("Bearer secret-token", request.Headers.Authorization!.ToString());
            Assert.NotNull(task.Id);
        }
    }

    [Fact]
    public async Task Unsigned_config_gets_no_signature_headers()
    {
        await using var host = await TestAgentHost.StartAsync();
        await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(Message.FromUser("task"), new SendMessageConfiguration
        {
            TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = "http://hooks.test/plain", Token = "t" },
        });
        await host.WaitIdleAsync();
        var call = host.Webhooks.Calls.First(c => c.Request.RequestUri!.AbsolutePath == "/plain");
        Assert.False(call.Request.Headers.Contains(WebhookSignature.SignatureHeader));
    }

    [Fact]
    public async Task Signing_secret_never_sent_or_returned()
    {
        await using var host = await TestAgentHost.StartAsync();
        var whsec = NewSecret();
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync(Message.FromUser("input"))).RequireTask(); // waits for input: still open
        await host.WaitIdleAsync();

        var created = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig
        {
            TaskId = task.Id, Url = "http://hooks.test/signed",
            Authentication = new AuthenticationInfo { Scheme = WebhookSignature.Scheme, Credentials = whsec },
        });
        Assert.Equal(WebhookSignature.Scheme, created.Authentication!.Scheme);
        Assert.True(string.IsNullOrEmpty(created.Authentication.Credentials));

        var got = await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = task.Id, Id = created.Id! });
        Assert.True(string.IsNullOrEmpty(got.Authentication!.Credentials));
        var listed = (await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id })).Configs.Single();
        Assert.True(string.IsNullOrEmpty(listed.Authentication!.Credentials));

        // Stored protected, not in plain.
        var stored = await host.App.Services.GetRequiredService<PushConfigStore>().GetAsync(new TaskKey("", "alice", task.Id), created.Id!);
        Assert.NotNull(stored);
        Assert.False(string.IsNullOrEmpty(stored.Config.Authentication!.Credentials));
        Assert.DoesNotContain(whsec, stored.Config.Authentication.Credentials);

        // A later event is delivered signed, and the secret itself is on no request header.
        await client.SendMessageAsync(Message.FromUser("more", taskId: task.Id));
        await host.WaitIdleAsync();
        var calls = host.Webhooks.Calls.Where(c => c.Request.RequestUri!.AbsolutePath == "/signed").ToList();
        Assert.NotEmpty(calls);
        var secret = Secret(whsec);
        foreach (var (request, body) in calls)
        {
            Assert.Null(request.Headers.Authorization);
            Assert.DoesNotContain(request.Headers, h => h.Value.Any(v => v.Contains(whsec) || v.Contains(WebhookSignature.SecretPrefix)));
            Assert.DoesNotContain(whsec, body);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            Assert.Equal(PushValidationResult.Valid, new PushNotificationReceiver().VerifySignature(headers, Encoding.UTF8.GetBytes(body), secret));
        }
    }

    [Theory]
    [InlineData("whsec_c2hvcnQ=")] // 5 bytes
    [InlineData("MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw")] // no whsec_ prefix
    [InlineData("")]
    public async Task Short_secret_rejected_at_create(string credentials)
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync(Message.FromUser("input"))).RequireTask();
        await host.WaitIdleAsync();
        var bad = new TaskPushNotificationConfig
        {
            TaskId = task.Id, Url = "http://hooks.test/x",
            Authentication = new AuthenticationInfo { Scheme = WebhookSignature.Scheme, Credentials = credentials },
        };

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.CreateTaskPushNotificationConfigAsync(bad));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Contains("Invalid webhook signing secret.", ex.Message);
        Assert.Empty((await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id })).Configs);

        // The inline config of SendMessage is checked the same way.
        var inline = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Message.FromUser("again", taskId: task.Id),
            new SendMessageConfiguration { TaskPushNotificationConfig = bad }));
        Assert.Equal(A2AErrorCode.InvalidParams, inline.Code);
    }
}
