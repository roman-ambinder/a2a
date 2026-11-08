using System.Net;
using System.Net.Http.Headers;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Retention;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

/// <summary>
/// <c>GET /artifacts/{tenant}/{taskId}/{objectId}</c>: only the caller who may read the task gets the bytes, and every other
/// case (other owner, other tenant, other task, purged task, bad id) looks the same: 404.
/// </summary>
public sealed class ArtifactEndpointTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();

    private sealed record Seed(string TaskId, string ObjectId)
    {
        public string Url(string tenant = "_") => $"/a2a/artifacts/{tenant}/{TaskId}/{ObjectId}";
    }

    private sealed class DenyGetTask : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(operation.Kind != A2AOperation.GetTask);
    }

    private sealed class FixedTenantResolver : ITenantResolver
    {
        public string Tenant { get; set; } = "";
        public string Resolve(HttpContext http) => Tenant;
    }

    /// <summary>Delegates to an in-memory store and can offer a presigned URL.</summary>
    private class WrappedStore : ArtifactObjectStore
    {
        public InMemoryArtifactObjectStore Inner { get; } = new();
        public Uri? Presigned { get; init; }
        public TimeSpan? RequestedLifetime { get; private set; }

        public override ValueTask<ArtifactObjectWriteResult> PutAsync(string key, Stream content, ArtifactObjectInfo info, long maxBytes, CancellationToken cancellationToken = default) =>
            Inner.PutAsync(key, content, info, maxBytes, cancellationToken);

        public override ValueTask<ArtifactObjectContent?> OpenReadAsync(string key, ArtifactByteRange? range = null, CancellationToken cancellationToken = default) =>
            Inner.OpenReadAsync(key, range, cancellationToken);

        public override ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => Inner.DeleteAsync(key, cancellationToken);

        public override IAsyncEnumerable<ArtifactObjectEntry> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
            Inner.ListAsync(prefix, cancellationToken);

        public override ValueTask<Uri?> TryCreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            RequestedLifetime = lifetime;
            return ValueTask.FromResult(Presigned);
        }
    }

    /// <summary>Sends one byte, then waits until it is canceled. Reports when it is disposed.</summary>
    private sealed class HangingStream(TaskCompletionSource disposed) : Stream
    {
        private bool _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[0] = 7;
                return 1;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) disposed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    private sealed class HangingStore(TaskCompletionSource disposed) : WrappedStore
    {
        public override ValueTask<ArtifactObjectContent?> OpenReadAsync(string key, ArtifactByteRange? range = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ArtifactObjectContent?>(new ArtifactObjectContent(new HangingStream(disposed), "application/octet-stream", null, 1000, 1000));
    }

    private static Task<TestAgentHost> StartAsync(ArtifactObjectStore store, Action<A2AServerOptions>? configure = null,
        Action<IServiceCollection>? services = null, bool requireAuthOnRoute = true) =>
        TestAgentHost.StartAsync(
            o =>
            {
                o.ArtifactStorage.PublicBaseUrl = new Uri(TestAgentHost.BaseUrl);
                configure?.Invoke(o);
            },
            services: s =>
            {
                s.AddSingleton(store);
                services?.Invoke(s);
            },
            configurePipeline: app =>
            {
                var group = app.MapA2AArtifacts("/a2a");
                if (requireAuthOnRoute) group.RequireAuthorization();
            });

    /// <summary>Creates a task for <paramref name="user"/> and stores an object under it.</summary>
    private static async Task<Seed> SeedAsync(TestAgentHost host, ArtifactObjectStore store, string user = "alice", string tenant = "")
    {
        var task = (await host.Client(ProtocolBindings.JsonRpc, user).SendMessageAsync("task")).RequireTask();
        return await PutAsync(store, task.Id, tenant);
    }

    private static async Task<Seed> PutAsync(ArtifactObjectStore store, string taskId, string tenant = "")
    {
        var objectId = ArtifactObjectKey.NewObjectId();
        await store.PutAsync(ArtifactObjectKey.Create(tenant, taskId, objectId), new MemoryStream(Payload),
            new ArtifactObjectInfo("application/pdf", "report.pdf"), long.MaxValue);
        return new Seed(taskId, objectId);
    }

    [Fact]
    public async Task Owner_downloads_bytes()
    {
        var store = new InMemoryArtifactObjectStore();
        await using var host = await StartAsync(store);
        var seed = await SeedAsync(host, store);

        using var response = await host.Http("alice").GetAsync(seed.Url());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Payload, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("report.pdf", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("private", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Another_owner_gets_404()
    {
        var store = new InMemoryArtifactObjectStore();
        await using var host = await StartAsync(store);
        var seed = await SeedAsync(host, store, "alice");

        using var response = await host.Http("bob").GetAsync(seed.Url());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Another_tenant_gets_404()
    {
        var store = new InMemoryArtifactObjectStore();
        await using var host = await StartAsync(store);
        var seed = await SeedAsync(host, store);

        using var response = await host.Http("alice").GetAsync(seed.Url("acme"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Resolver_tenant_differing_from_link_gets_404()
    {
        var store = new InMemoryArtifactObjectStore();
        var resolver = new FixedTenantResolver();
        await using var host = await StartAsync(store, services: s => s.Replace(ServiceDescriptor.Singleton<ITenantResolver>(resolver)));
        var seed = await SeedAsync(host, store);

        resolver.Tenant = "other";
        using var response = await host.Http("alice").GetAsync(seed.Url());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Resolver_tenant_equal_to_link_is_served()
    {
        var store = new InMemoryArtifactObjectStore();
        var resolver = new FixedTenantResolver { Tenant = "acme" };
        await using var host = await StartAsync(store, services: s => s.Replace(ServiceDescriptor.Singleton<ITenantResolver>(resolver)));
        var seed = await SeedAsync(host, store, tenant: "acme");

        using var response = await host.Http("alice").GetAsync(seed.Url("acme"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Payload, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Purged_task_gets_404()
    {
        var store = new InMemoryArtifactObjectStore();
        await using var host = await StartAsync(store);
        var seed = await SeedAsync(host, store);
        Assert.True(await host.App.Services.GetRequiredService<A2ATaskAdmin>().DeleteTaskAsync(new TaskKey("", "alice", seed.TaskId)));

        using var response = await host.Http("alice").GetAsync(seed.Url());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Object_id_from_another_task_gets_404()
    {
        var store = new InMemoryArtifactObjectStore();
        await using var host = await StartAsync(store);
        var first = await SeedAsync(host, store);
        var second = await SeedAsync(host, store);

        using var response = await host.Http("alice").GetAsync($"/a2a/artifacts/_/{second.TaskId}/{first.ObjectId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("..%2F..%2Fsecret")]
    public async Task Malformed_object_id_gets_404(string objectId)
    {
        var store = new InMemoryArtifactObjectStore();
        await using var host = await StartAsync(store);
        var seed = await SeedAsync(host, store);

        using var response = await host.Http("alice").GetAsync($"/a2a/artifacts/_/{seed.TaskId}/{objectId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_is_rejected()
    {
        var store = new InMemoryArtifactObjectStore();
        await using var host = await StartAsync(store);
        var seed = await SeedAsync(host, store);

        using var response = await host.Http().GetAsync(seed.Url());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Handler_denying_GetTask_denies_download()
    {
        var store = new InMemoryArtifactObjectStore();
        await using var host = await StartAsync(store,
            services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, DenyGetTask>()));
        var seed = await SeedAsync(host, store);

        using var response = await host.Http("alice").GetAsync(seed.Url());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // a denied task read is reported as TaskNotFound
    }

    [Fact]
    public async Task Startup_fails_without_authorization_policy()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartAsync(new InMemoryArtifactObjectStore(), requireAuthOnRoute: false));

        Assert.Contains("RequireAuthorization", ex.Message);
    }

    [Fact]
    public async Task Redirect_mode_returns_302_to_presigned_url()
    {
        var store = new WrappedStore { Presigned = new Uri("https://cdn.test/o?sig=1") };
        await using var host = await StartAsync(store);
        var seed = await SeedAsync(host, store);

        using var response = await host.Http("alice").GetAsync(seed.Url());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(new Uri("https://cdn.test/o?sig=1"), response.Headers.Location);
        Assert.Equal(TimeSpan.FromMinutes(5), store.RequestedLifetime);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Redirect_mode_proxies_when_store_has_no_presigned_url()
    {
        var store = new WrappedStore { Presigned = null };
        await using var host = await StartAsync(store);
        var seed = await SeedAsync(host, store);

        using var response = await host.Http("alice").GetAsync(seed.Url());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Payload, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Proxy_mode_never_redirects()
    {
        var store = new WrappedStore { Presigned = new Uri("https://cdn.test/o?sig=1") };
        await using var host = await StartAsync(store, o => o.ArtifactStorage.Delivery = ArtifactDelivery.Proxy);
        var seed = await SeedAsync(host, store);

        using var response = await host.Http("alice").GetAsync(seed.Url());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(store.RequestedLifetime);
    }

    [Fact]
    public async Task Proxy_mode_honours_Range()
    {
        var store = new InMemoryArtifactObjectStore();
        await using var host = await StartAsync(store, o => o.ArtifactStorage.Delivery = ArtifactDelivery.Proxy);
        var seed = await SeedAsync(host, store);
        using var request = new HttpRequestMessage(HttpMethod.Get, seed.Url());
        request.Headers.Range = new RangeHeaderValue(10, 19);

        using var response = await host.Http("alice").SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new ContentRangeHeaderValue(10, 19, 1000), response.Content.Headers.ContentRange);
        Assert.Equal(Payload[10..20], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Range_beyond_the_object_gets_416()
    {
        var store = new InMemoryArtifactObjectStore();
        await using var host = await StartAsync(store, o => o.ArtifactStorage.Delivery = ArtifactDelivery.Proxy);
        var seed = await SeedAsync(host, store);
        using var request = new HttpRequestMessage(HttpMethod.Get, seed.Url());
        request.Headers.Range = new RangeHeaderValue(5000, null);

        using var response = await host.Http("alice").SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
    }

    [Fact]
    public async Task Redirect_mode_proxies_a_ranged_request()
    {
        var store = new WrappedStore { Presigned = new Uri("https://cdn.test/o?sig=1") };
        await using var host = await StartAsync(store);
        var seed = await SeedAsync(host, store);
        using var request = new HttpRequestMessage(HttpMethod.Get, seed.Url());
        request.Headers.Range = new RangeHeaderValue(0, 9);

        using var response = await host.Http("alice").SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Null(store.RequestedLifetime);
    }

    [Fact]
    public async Task Client_abort_disposes_object_stream()
    {
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new HangingStore(disposed);
        await using var host = await StartAsync(store, o => o.ArtifactStorage.Delivery = ArtifactDelivery.Proxy);
        var seed = await SeedAsync(host, store);
        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, seed.Url());

        using var response = await host.Http("alice").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
        Assert.Equal(1, await body.ReadAsync(new byte[1], cts.Token));
        Assert.False(disposed.Task.IsCompleted);
        await cts.CancelAsync();
        response.Dispose();

        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
