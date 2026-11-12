using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using A2A.Integrity;
using A2A.Json;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace A2A.Tests;

/// <summary>
/// Inbound uploads: <c>POST /uploads/{tenant}</c>, <c>GET /uploads/{tenant}/{objectId}</c>, the message check and the agent read.
/// Only the owner reaches a file, and every refusal leaves nothing behind.
/// </summary>
public sealed class UploadEndpointTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();

    /// <summary>An in-memory store that counts writes, can hold a write open, and can run something inside the first upload write.</summary>
    private sealed class Store(TimeProvider? time = null) : ArtifactObjectStore
    {
        public InMemoryArtifactObjectStore Inner { get; } = new(time);
        public TaskCompletionSource PutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Task>? DuringFirstUpload { get; set; }
        private int _uploadPuts;

        /// <summary>How many upload writes began.</summary>
        public int UploadPutCount => Volatile.Read(ref _uploadPuts);

        public override async ValueTask<ArtifactObjectWriteResult> PutAsync(string key, Stream content, ArtifactObjectInfo info, long maxBytes, CancellationToken cancellationToken = default)
        {
            if (key.StartsWith("_uploads/", StringComparison.Ordinal))
            {
                PutEntered.TrySetResult();
                if (Interlocked.Increment(ref _uploadPuts) == 1 && DuringFirstUpload is { } hook) await hook();
            }
            return await Inner.PutAsync(key, content, info, maxBytes, cancellationToken);
        }

        public override ValueTask<ArtifactObjectContent?> OpenReadAsync(string key, ArtifactByteRange? range = null, CancellationToken cancellationToken = default) =>
            Inner.OpenReadAsync(key, range, cancellationToken);

        public override ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => Inner.DeleteAsync(key, cancellationToken);

        public override IAsyncEnumerable<ArtifactObjectEntry> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
            Inner.ListAsync(prefix, cancellationToken);

        public async Task<List<string>> KeysAsync(string prefix)
        {
            var keys = new List<string>();
            await foreach (var e in Inner.ListAsync(prefix)) keys.Add(e.Key);
            return keys;
        }
    }

    private sealed class FixedTenantResolver : ITenantResolver
    {
        public string Tenant { get; set; } = "";
        public string Resolve(HttpContext http) => Tenant;
    }

    private sealed class DenyExtension(string name) : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(!(operation.Kind == A2AOperation.Extension && operation.Name == name));
    }

    private sealed class AllowFiles : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(operation.Kind != A2AOperation.Extension || operation.Name is A2ARequestHandler.UploadOperationName or A2ARequestHandler.ReadUploadOperationName);
    }

    /// <summary>A custom handler that does not know the two names: it denies every extension operation, as the base class does.</summary>
    private sealed class DenyAllExtensions : A2AAuthorizationHandler;

    /// <summary>A body with no length that sends <c>first</c> bytes, tells the test, and waits for the release before it sends the rest.</summary>
    private sealed class HeldContent(int first, int rest, TaskCompletionSource release) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[first]);
            await stream.FlushAsync();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await stream.WriteAsync(new byte[rest]);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    /// <summary>A body with no length (so no Content-Length is sent).</summary>
    private sealed class UnsizedContent(int size) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[1024];
            for (var sent = 0; sent < size; sent += chunk.Length) await stream.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, size - sent)));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private static Task<TestAgentHost> StartAsync(Store store, Action<A2AServerOptions>? configure = null, Action<IServiceCollection>? services = null,
        bool requireAuthOnRoute = true, bool enabled = true, bool map = true, TimeProvider? clock = null) =>
        TestAgentHost.StartAsync(
            o =>
            {
                o.ArtifactStorage.PublicBaseUrl = new Uri(TestAgentHost.BaseUrl);
                o.ArtifactStorage.Uploads.Enabled = enabled;
                configure?.Invoke(o);
            },
            services: s =>
            {
                s.AddSingleton<ArtifactObjectStore>(store);
                if (clock is not null) s.AddSingleton(clock);
                services?.Invoke(s);
            },
            configurePipeline: app =>
            {
                if (!map) return;
                var group = app.MapA2AUploads("/a2a");
                if (requireAuthOnRoute) group.RequireAuthorization();
            });

    private static ByteArrayContent Body(byte[] bytes, string mediaType = "application/pdf", string? fileName = "report.pdf")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        if (fileName is not null) content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName };
        return content;
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient http, HttpContent content, string tenant = "_") =>
        http.PostAsync($"/a2a/uploads/{tenant}", content);

    private static async Task<UrlPart> PartOf(HttpResponseMessage response) =>
        Assert.IsType<UrlPart>(A2AJson.Deserialize<Part>(await response.Content.ReadAsStringAsync()));

    private static async Task<UrlPart> UploadOkAsync(TestAgentHost host, string user = "alice", string tenant = "_", byte[]? bytes = null)
    {
        using var response = await UploadAsync(host.Http(user), Body(bytes ?? Payload, "text/plain", "report.txt"), tenant);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await PartOf(response);
    }

    private static string ObjectIdOf(UrlPart part) => part.Url.AbsolutePath[(part.Url.AbsolutePath.LastIndexOf('/') + 1)..];

    private static Message UserMessage(string text, params Part[] parts) =>
        new() { MessageId = Guid.NewGuid().ToString(), Role = Role.User, Parts = [Part.FromText(text), .. parts] };

    private static async Task<string> RunAsync(TestAgentHost host, Message message, string user = "alice", string binding = ProtocolBindings.JsonRpc)
    {
        var task = (await host.Client(binding, user).SendMessageAsync(message)).RequireTask();
        await host.WaitIdleAsync();
        var done = await host.Client(binding, user).GetTaskAsync(task.Id);
        return done.Status.Message!.GetText();
    }

    [Fact]
    public async Task Upload_answers_201_with_the_part_and_a_Location_and_the_owner_reads_it_back()
    {
        var store = new Store();
        await using var host = await StartAsync(store);

        using var response = await UploadAsync(host.Http("alice"), Body(Payload));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var part = await PartOf(response);
        Assert.Equal(part.Url, response.Headers.Location);
        Assert.StartsWith($"{TestAgentHost.BaseUrl}/uploads/_/", part.Url.AbsoluteUri);
        Assert.Equal("application/pdf", part.MediaType);
        Assert.Equal("report.pdf", part.Filename);
        Assert.Equal(ArtifactHash.Format(SHA256.HashData(Payload)), part.Metadata!["sha256"].GetString());
        Assert.Equal(Payload.Length, part.Metadata["sizeBytes"].GetInt64());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());

        using var download = await host.Http("alice").GetAsync(part.Url.AbsolutePath);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Payload, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("report.pdf", download.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        var request = new HttpRequestMessage(HttpMethod.Get, part.Url.AbsolutePath);
        request.Headers.Range = new RangeHeaderValue(10, 19);
        using var ranged = await host.Http("alice").SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(Payload.AsSpan(10, 10).ToArray(), await ranged.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_body_larger_than_MaxRequestBodyBytes_is_accepted()
    {
        var store = new Store();
        await using var host = await StartAsync(store, o => o.Limits.MaxRequestBodyBytes = 1024);
        var big = new byte[200_000];

        using var response = await UploadAsync(host.Http("alice"), Body(big));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(big.Length, (await PartOf(response)).Metadata!["sizeBytes"].GetInt64());
    }

    [Fact]
    public async Task Anonymous_is_refused_even_when_anonymous_calls_are_allowed()
    {
        var store = new Store();
        await using var host = await StartAsync(store, o => o.AllowAnonymous = true, requireAuthOnRoute: false);
        var part = await UploadOkAsync(host, "alice");

        using var upload = await UploadAsync(host.Http(), Body(Payload));
        using var read = await host.Http().GetAsync(part.Url.AbsolutePath);

        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
        Assert.Single(await store.KeysAsync("_uploads/")); // only alice's
    }

    [Theory]
    [InlineData(A2ARequestHandler.UploadOperationName)]
    [InlineData(A2ARequestHandler.ReadUploadOperationName)]
    public async Task Custom_authorization_handler_can_deny_each_operation_by_name(string name)
    {
        var store = new Store();
        await using var host = await StartAsync(store,
            services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler>(new DenyExtension(name))));

        using var upload = await UploadAsync(host.Http("alice"), Body(Payload));
        if (name == A2ARequestHandler.UploadOperationName)
        {
            Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
            Assert.Empty(await store.KeysAsync("_uploads/"));
        }
        else
        {
            var part = await PartOf(upload);
            using var read = await host.Http("alice").GetAsync(part.Url.AbsolutePath);
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        }
    }

    [Fact]
    public async Task A_custom_handler_must_allow_the_two_names()
    {
        var denied = new Store();
        await using (var host = await StartAsync(denied, services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, DenyAllExtensions>())))
        {
            using var response = await UploadAsync(host.Http("alice"), Body(Payload));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var allowed = new Store();
        await using (var host = await StartAsync(allowed, services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, AllowFiles>())))
        {
            var part = await UploadOkAsync(host);
            using var read = await host.Http("alice").GetAsync(part.Url.AbsolutePath);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
    }

    [Fact]
    public async Task Route_tenant_disagreeing_with_the_resolver_is_refused()
    {
        var store = new Store();
        var resolver = new FixedTenantResolver { Tenant = "acme" };
        await using var host = await StartAsync(store, services: s => s.Replace(ServiceDescriptor.Singleton<ITenantResolver>(resolver)));

        using var wrongUpload = await UploadAsync(host.Http("alice"), Body(Payload), "other");
        using var defaultUpload = await UploadAsync(host.Http("alice"), Body(Payload), "_");
        Assert.Equal(HttpStatusCode.BadRequest, wrongUpload.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, defaultUpload.StatusCode);
        Assert.Empty(await store.KeysAsync("_uploads/"));

        var part = await UploadOkAsync(host, tenant: "acme");
        Assert.Contains("/uploads/acme/", part.Url.AbsoluteUri);
        using var wrongRead = await host.Http("alice").GetAsync($"/a2a/uploads/other/{ObjectIdOf(part)}");
        using var read = await host.Http("alice").GetAsync(part.Url.AbsolutePath);
        Assert.Equal(HttpStatusCode.NotFound, wrongRead.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Theory]
    [InlineData("bob", "_")]
    [InlineData("alice", "acme")]
    public async Task Another_owner_or_tenant_or_malformed_id_gets_404(string user, string tenant)
    {
        var store = new Store();
        await using var host = await StartAsync(store);
        var part = await UploadOkAsync(host, "alice");

        using var response = await host.Http(user).GetAsync($"/a2a/uploads/{tenant}/{ObjectIdOf(part)}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_object_id_gets_404()
    {
        var store = new Store();
        await using var host = await StartAsync(store);

        using var response = await host.Http("alice").GetAsync("/a2a/uploads/_/NOT-AN-ID");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Content_Length_over_the_cap_is_413_before_any_write()
    {
        var store = new Store();
        await using var host = await StartAsync(store, o => o.ArtifactStorage.Uploads.MaxUploadBytes = 1000);

        using var response = await UploadAsync(host.Http("alice"), Body(new byte[2000]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(store.PutEntered.Task.IsCompleted);
        Assert.Empty(await store.KeysAsync(string.Empty));
    }

    [Fact]
    public async Task A_chunked_body_over_the_cap_is_413_and_leaves_no_object()
    {
        var store = new Store();
        await using var host = await StartAsync(store, o => o.ArtifactStorage.Uploads.MaxUploadBytes = 1000);
        using var content = new UnsizedContent(5000);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");

        using var response = await UploadAsync(host.Http("alice"), content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(store.PutEntered.Task.IsCompleted); // the write began, and was cut off
        Assert.Empty(await store.KeysAsync(string.Empty));
    }

    [Fact]
    public async Task Store_quota_is_429_with_Retry_After_and_leaves_no_object()
    {
        var store = new Store();
        await using var host = await StartAsync(store, o => o.ArtifactStorage.Uploads.MaxStoredBytesPerCaller = 1500);
        await UploadOkAsync(host, bytes: new byte[1000]);

        using var response = await UploadAsync(host.Http("alice"), Body(new byte[1000]));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromHours(6), response.Headers.RetryAfter?.Delta);
        Assert.Single(await store.KeysAsync("_uploads/"));
        using var other = await UploadAsync(host.Http("bob"), Body(new byte[1000])); // a quota of its own
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task Count_quota_is_429()
    {
        var store = new Store();
        await using var host = await StartAsync(store, o => o.ArtifactStorage.Uploads.MaxUploadsPerCaller = 1);
        await UploadOkAsync(host);

        using var response = await UploadAsync(host.Http("alice"), Body(Payload));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(await store.KeysAsync("_uploads/"));
    }

    [Fact]
    public async Task Too_many_uploads_at_once_is_429_with_a_one_second_Retry_After()
    {
        var store = new Store();
        await using var host = await StartAsync(store, o => o.ArtifactStorage.Uploads.MaxConcurrentUploadsPerCaller = 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var held = new HeldContent(10, 10, release);
        held.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        var first = host.Http("alice").PostAsync("/a2a/uploads/_", held);
        await store.PutEntered.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the first upload holds the caller's slot

        using var second = await UploadAsync(host.Http("alice"), Body(Payload));
        using var otherCaller = await UploadAsync(host.Http("bob"), Body(Payload));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), second.Headers.RetryAfter?.Delta);
        Assert.Equal(HttpStatusCode.Created, otherCaller.StatusCode);
        release.SetResult();
        using var done = await first;
        Assert.Equal(HttpStatusCode.Created, done.StatusCode);
        using var after = await UploadAsync(host.Http("alice"), Body(Payload)); // the slot is free again
        Assert.Equal(HttpStatusCode.Created, after.StatusCode);
    }

    [Fact]
    public async Task A_racing_write_that_pushes_the_total_over_the_limit_deletes_the_new_file()
    {
        var store = new Store();
        await using var host = await StartAsync(store, o => o.ArtifactStorage.Uploads.MaxUploadsPerCaller = 1);
        var rival = UploadObjectKey.CreateUpload("", UploadObjectKey.OwnerHash("alice"), ArtifactObjectKey.NewObjectId());
        // Another instance stores a file for the same caller after this upload passed the quota check.
        store.DuringFirstUpload = async () => await store.Inner.PutAsync(rival, new MemoryStream([1]), new ArtifactObjectInfo(null, null), 10);

        using var response = await UploadAsync(host.Http("alice"), Body(Payload));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(new[] { rival }, await store.KeysAsync("_uploads/"));
    }

    [Theory]
    [InlineData("a%01b.txt")]
    [InlineData("..")]
    public async Task An_unsafe_file_name_is_refused(string encoded)
    {
        var store = new Store();
        await using var host = await StartAsync(store);
        using var content = new ByteArrayContent(Payload);
        content.Headers.TryAddWithoutValidation("Content-Disposition", $"attachment; filename*=UTF-8''{encoded}");

        using var response = await UploadAsync(host.Http("alice"), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await store.KeysAsync(string.Empty));
    }

    [Fact]
    public async Task A_too_long_file_name_is_refused_and_a_path_is_removed()
    {
        var store = new Store();
        await using var host = await StartAsync(store);

        using var tooLong = await UploadAsync(host.Http("alice"), Body(Payload, fileName: new string('x', 256)));
        using var withPath = await UploadAsync(host.Http("alice"), Body(Payload, fileName: "../../etc/passwd"));

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.Created, withPath.StatusCode);
        Assert.Equal("passwd", (await PartOf(withPath)).Filename);
        Assert.Single(await store.KeysAsync("_uploads/"));
    }

    [Fact]
    public async Task An_unparsable_media_type_is_refused()
    {
        var store = new Store();
        await using var host = await StartAsync(store);
        using var content = new ByteArrayContent(Payload);
        content.Headers.TryAddWithoutValidation("Content-Type", "not a media type");

        using var response = await UploadAsync(host.Http("alice"), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await store.KeysAsync(string.Empty));
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task A_message_with_the_owners_own_link_is_accepted_and_the_agent_reads_the_file(string binding)
    {
        var store = new Store();
        await using var host = await StartAsync(store);
        var part = await UploadOkAsync(host);

        var result = await RunAsync(host, UserMessage("upload", part), binding: binding);

        Assert.Equal($"read:{Payload.Length}:report.txt", result);
        var references = await store.KeysAsync("_uploadrefs/");
        var reference = Assert.Single(references);
        Assert.True(UploadObjectKey.TryParseReference(reference, out _, out var ownerHash, out var objectId, out _));
        Assert.Equal(UploadObjectKey.OwnerHash("alice"), ownerHash);
        Assert.Equal(ObjectIdOf(part), objectId);
    }

    [Fact]
    public async Task A_follow_up_with_the_owners_own_link_is_accepted_and_one_with_a_foreign_link_is_not()
    {
        var store = new Store();
        await using var host = await StartAsync(store);
        var mine = await UploadOkAsync(host, "alice");
        var theirs = await UploadOkAsync(host, "bob");
        var client = host.Client(ProtocolBindings.JsonRpc, "alice");
        var task = (await client.SendMessageAsync(UserMessage("input"))).RequireTask();
        await host.WaitIdleAsync();

        var historyBefore = (await client.GetTaskAsync(task.Id)).History!.Count;
        var refused = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            var message = UserMessage("upload", theirs);
            message.TaskId = task.Id;
            await client.SendMessageAsync(message);
        });
        Assert.Equal(A2AErrorCode.InvalidParams, refused.Code);
        Assert.Equal(historyBefore, (await client.GetTaskAsync(task.Id)).History!.Count); // the refused message was not stored
        Assert.Empty(await store.KeysAsync("_uploadrefs/"));

        var followUp = UserMessage("upload", mine);
        followUp.TaskId = task.Id;
        await client.SendMessageAsync(followUp);
        await host.WaitIdleAsync();
        Assert.True((await client.GetTaskAsync(task.Id)).History!.Count > historyBefore); // the follow-up was stored
        var reference = Assert.Single(await store.KeysAsync("_uploadrefs/"));
        Assert.True(UploadObjectKey.TryParseReference(reference, out _, out _, out _, out var taskId));
        Assert.Equal(task.Id, taskId);
    }

    public enum Refusal { OtherOwner, OtherTenant, Missing, NotCanonical, Expired }

    [Theory]
    [InlineData(Refusal.OtherOwner)]
    [InlineData(Refusal.OtherTenant)]
    [InlineData(Refusal.Missing)]
    [InlineData(Refusal.NotCanonical)]
    [InlineData(Refusal.Expired)]
    public async Task A_message_with_a_link_that_is_not_the_senders_own_is_refused_and_creates_no_task(Refusal refusal)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new Store(clock);
        await using var host = await StartAsync(store, clock: clock);
        var alices = await UploadOkAsync(host, "alice");
        var link = refusal switch
        {
            Refusal.OtherOwner => alices.Url,
            Refusal.OtherTenant => ArtifactLink.CreateUpload(new Uri(TestAgentHost.BaseUrl), "acme", ObjectIdOf(alices)),
            Refusal.Missing => ArtifactLink.CreateUpload(new Uri(TestAgentHost.BaseUrl), "", ArtifactObjectKey.NewObjectId()),
            Refusal.NotCanonical => new Uri(alices.Url + "?x=1"),
            _ => alices.Url,
        };
        var sender = refusal is Refusal.OtherOwner ? "bob" : "alice";
        if (refusal == Refusal.Expired) clock.Advance(new A2AServerOptions().ArtifactStorage.Uploads.UnusedUploadTtl + TimeSpan.FromMinutes(1));
        var client = host.Client(ProtocolBindings.JsonRpc, sender);

        var ex = await Assert.ThrowsAsync<A2AException>(async () => await client.SendMessageAsync(UserMessage("upload", Part.FromUrl(link))));

        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal("The message refers to an uploaded file that does not exist, is not yours, or has expired.", ex.Message);
        Assert.Empty((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
        Assert.Empty(await store.KeysAsync("_uploadrefs/"));
    }

    [Fact]
    public async Task OpenUploadAsync_returns_null_for_an_external_url_and_for_another_owners_file()
    {
        var store = new Store();
        await using var host = await StartAsync(store);
        var alices = await UploadOkAsync(host, "alice");

        var external = await RunAsync(host, UserMessage("upload", Part.FromUrl(new Uri("https://example.com/file.bin"))));
        // A link written in the text never went through the message check: the read itself must refuse another owner's file.
        var foreign = await RunAsync(host, UserMessage($"upload-link {alices.Url}"), user: "bob");
        var own = await RunAsync(host, UserMessage($"upload-link {alices.Url}"), user: "alice");

        Assert.Equal("no-upload", external);
        Assert.Equal("no-upload", foreign);
        Assert.Equal($"read:{Payload.Length}:report.txt", own);
    }

    [Fact]
    public async Task With_uploads_disabled_url_parts_are_left_alone()
    {
        var store = new Store();
        await using var host = await StartAsync(store, enabled: false, map: false);
        var link = ArtifactLink.CreateUpload(new Uri(TestAgentHost.BaseUrl), "", ArtifactObjectKey.NewObjectId());

        var result = await RunAsync(host, UserMessage("upload", Part.FromUrl(link)));

        Assert.Equal("no-upload", result); // accepted as an ordinary URL; the agent read is off
        Assert.Empty(await store.KeysAsync(string.Empty));
    }

    [Fact]
    public async Task MapA2AUploads_throws_when_uploads_are_disabled()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(new Store(), enabled: false));

        Assert.Contains("Uploads.Enabled", ex.Message);
    }

    [Fact]
    public async Task MapA2AUploads_throws_without_an_object_store()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TestAgentHost.StartAsync(
            o => o.ArtifactStorage.Uploads.Enabled = true,
            configurePipeline: app => app.MapA2AUploads("/a2a").RequireAuthorization()));

        Assert.Contains("ArtifactObjectStore", ex.Message);
    }

    [Fact]
    public async Task MapA2AUploads_throws_for_an_option_out_of_range()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(new Store(),
            o => o.ArtifactStorage.Uploads.MaxUploadBytes = o.ArtifactStorage.MaxObjectBytes + 1));

        Assert.Contains("MaxUploadBytes", ex.Message);
    }

    [Fact]
    public async Task Startup_fails_without_an_authorization_policy_on_the_upload_routes()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(new Store(), requireAuthOnRoute: false));

        Assert.Contains("RequireAuthorization", ex.Message);
    }

    [Fact]
    public async Task A_file_name_whose_encoded_form_is_over_1024_characters_is_refused()
    {
        var store = new Store();
        await using var host = await StartAsync(store);
        var name = new string('\u00e9', 200); // 200 characters, but 1200 once percent-encoded

        using var response = await UploadAsync(host.Http("alice"), Body(Payload, fileName: name));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await store.KeysAsync(string.Empty));
    }

    [Fact]
    public async Task Content_Length_above_the_remaining_quota_is_429_before_the_body_is_taken()
    {
        var store = new Store();
        await using var host = await StartAsync(store, o => o.ArtifactStorage.Uploads.MaxStoredBytesPerCaller = 1500);
        await UploadOkAsync(host, bytes: new byte[1000]);
        Assert.Equal(1, store.UploadPutCount);

        using var response = await UploadAsync(host.Http("alice"), Body(new byte[600])); // 500 bytes are left

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(1, store.UploadPutCount); // no write began
        Assert.Single(await store.KeysAsync("_uploads/"));
    }

    // ---- A real Kestrel server: TestServer has no IHttpMaxRequestBodySizeFeature, so only this proves the raised body limit. ----

    /// <summary>Zeros of a given size, produced while sending (chunked): the test holds no large array.</summary>
    private sealed class GeneratedContent(long size) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[1024 * 1024];
            for (long sent = 0; sent < size;)
            {
                var n = (int)Math.Min(chunk.Length, size - sent);
                try { await stream.WriteAsync(chunk.AsMemory(0, n)); }
                catch (IOException) { return; } // the server answered and closed early
                sent += n;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    private static async Task<(WebApplication App, HttpClient Http)> StartKestrelAsync(Store store, long maxUploadBytes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = TestAgentHost.DefaultCard();
            o.ArtifactStorage.PublicBaseUrl = new Uri(TestAgentHost.BaseUrl);
            o.ArtifactStorage.Uploads.Enabled = true;
            o.ArtifactStorage.Uploads.MaxUploadBytes = maxUploadBytes;
        }).AddAgentExecutor<ScenarioAgent>();
        builder.Services.AddA2AServerAspNetCore();
        builder.Services.AddSingleton<ArtifactObjectStore>(store);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapA2AUploads("/a2a").RequireAuthorization();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Add("X-Test-User", "alice");
        return (app, http);
    }

    [Fact]
    public async Task On_real_Kestrel_a_body_above_the_30_MB_default_is_accepted()
    {
        const long size = 40L * 1024 * 1024;
        var store = new Store();
        var (app, http) = await StartKestrelAsync(store, maxUploadBytes: 50L * 1024 * 1024);
        await using var _ = app;
        using var _http = http;
        using var content = new GeneratedContent(size);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");

        using var response = await http.PostAsync("/a2a/uploads/_", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(size, (await PartOf(response)).Metadata!["sizeBytes"].GetInt64());
        Assert.Single(await store.KeysAsync("_uploads/"));
    }

    [Fact]
    public async Task On_real_Kestrel_a_chunked_body_above_MaxUploadBytes_is_413_and_leaves_no_object()
    {
        var store = new Store();
        var (app, http) = await StartKestrelAsync(store, maxUploadBytes: 5L * 1024 * 1024);
        await using var _ = app;
        using var _http = http;

        // A raw socket: the server answers and closes while the client is still sending, and HttpClient would throw on that write
        // instead of showing the answer. Here the answer is read on its own while the chunks are written.
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(http.BaseAddress!.Host, http.BaseAddress.Port);
        var net = tcp.GetStream();
        var head = $"POST /a2a/uploads/_ HTTP/1.1\r\nHost: {http.BaseAddress.Authority}\r\nX-Test-User: alice\r\n" +
                   "Content-Type: application/octet-stream\r\nTransfer-Encoding: chunked\r\n\r\n";
        await net.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head));
        var writer = Task.Run(async () =>
        {
            var chunk = new byte[1024 * 1024];
            var prefix = System.Text.Encoding.ASCII.GetBytes($"{chunk.Length:x}\r\n");
            var crlf = "\r\n"u8.ToArray();
            try
            {
                for (var i = 0; i < 40; i++)
                {
                    await net.WriteAsync(prefix);
                    await net.WriteAsync(chunk);
                    await net.WriteAsync(crlf);
                }
                await net.WriteAsync("0\r\n\r\n"u8.ToArray());
            }
            catch (IOException)
            {
                // The server closed after its answer.
            }
        });
        using var reader = new StreamReader(net, System.Text.Encoding.ASCII);
        var statusLine = await reader.ReadLineAsync();
        await writer;

        Assert.StartsWith("HTTP/1.1 413", statusLine);
        Assert.Empty(await store.KeysAsync(string.Empty));
    }

    private sealed class CaptureLogger : ILoggerProvider, ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public async Task Startup_warns_when_uploads_are_on_and_retention_is_off(bool uploads, bool retention, bool warns)
    {
        var logs = new CaptureLogger();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = TestAgentHost.DefaultCard();
            o.ArtifactStorage.PublicBaseUrl = new Uri(TestAgentHost.BaseUrl);
            o.ArtifactStorage.Uploads.Enabled = uploads;
        });
        builder.Services.AddSingleton<ArtifactObjectStore>(new InMemoryArtifactObjectStore());
        if (retention) builder.Services.AddA2ARetention();
        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();

        lock (logs.Entries)
        {
            var matches = logs.Entries.Where(e => e.Message.Contains("Uploads.Enabled", StringComparison.Ordinal)).ToList();
            if (warns) Assert.Equal(LogLevel.Warning, Assert.Single(matches).Level);
            else Assert.Empty(matches);
        }
    }
}
