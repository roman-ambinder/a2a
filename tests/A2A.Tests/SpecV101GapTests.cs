using System.Net;
using A2A.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using A2A.Server;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

/// <summary>
///     Spec v1.0.1 SHOULDs: extended card caching (§13.3), no patch number in <c>A2A-Version</c> (§3.6), and which
///     permission a PermissionDenied is about (§3.3.2).
/// </summary>
public class SpecV101GapTests
{
    // ---- §13.3: extended card caching ----

    private static HttpRequestMessage ExtendedCardRequest(string? ifNoneMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/a2a/extendedAgentCard");
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return req;
    }

    [Fact]
    public async Task Extended_card_is_private_must_revalidate_and_has_an_entity_tag()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var http = host.Http("alice");

        using var resp = await http.SendAsync(ExtendedCardRequest());

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var cache = resp.Headers.CacheControl!;
        Assert.True(cache.Private);
        Assert.True(cache.NoCache);
        Assert.False(cache.Public);
        Assert.Contains("Authorization", resp.Headers.Vary);
        Assert.Contains("A2A-Version", resp.Headers.Vary);
        Assert.NotNull(resp.Headers.ETag);
        Assert.Contains("Extended card for alice", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Extended_card_honors_if_none_match()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var http = host.Http("alice");
        using var first = await http.SendAsync(ExtendedCardRequest());
        var tag = first.Headers.ETag!.Tag;

        using var same = await http.SendAsync(ExtendedCardRequest(tag));
        using var weak = await http.SendAsync(ExtendedCardRequest("W/" + tag));
        using var stale = await http.SendAsync(ExtendedCardRequest("\"something-else\""));

        Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);
        Assert.Empty(await same.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotModified, weak.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal(tag, stale.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Extended_card_entity_tag_differs_per_caller()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var alice = host.Http("alice");
        using var bob = host.Http("bob");
        using var a = await alice.SendAsync(ExtendedCardRequest());
        using var b = await bob.SendAsync(ExtendedCardRequest());

        Assert.NotEqual(a.Headers.ETag!.Tag, b.Headers.ETag!.Tag);
        // Bob cannot get a 304 for Alice's card: the tag is of his own card.
        using var cross = await bob.SendAsync(ExtendedCardRequest(a.Headers.ETag.Tag));
        Assert.Equal(HttpStatusCode.OK, cross.StatusCode);
        Assert.Contains("Extended card for bob", await cross.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Extended_card_still_requires_authentication_before_any_304()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var alice = host.Http("alice");
        using var first = await alice.SendAsync(ExtendedCardRequest());
        using var anonymous = host.Http();

        using var resp = await anonymous.SendAsync(ExtendedCardRequest(first.Headers.ETag!.Tag));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ---- §3.6: Major.Minor only ----

    [Theory]
    [InlineData("1.0", "1.0")]
    [InlineData("1.0.2", "1.0")]
    [InlineData(" 1.1.0 ", "1.1")]
    [InlineData("1.0.2.7", "1.0")]
    [InlineData("1", "1")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void ToMajorMinor_drops_the_patch(string? version, string? expected)
    {
        Assert.Equal(expected, A2AProtocol.ToMajorMinor(version));
    }

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson, ProtocolBindings.Grpc };

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_patch_version_in_the_options_is_sent_as_major_minor(string binding)
    {
        string? seen = null;
        await using var host = await TestAgentHost.StartAsync(grpc: true,
            configurePipeline: app => app.Use(async (http, next) =>
            {
                if (http.Request.Headers.TryGetValue(A2AProtocol.Headers.Version, out var v)) seen = v.ToString();
                await next();
            }));
        var client = host.Client(binding, options: new A2AClientOptions { ProtocolVersion = "1.0.3" });

        await client.SendMessageAsync("hello");

        Assert.Equal("1.0", seen);
    }

    // ---- §3.3.2: which permission is missing ----

    private sealed class DenyAll : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation,
            object request, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Permission_denied_names_the_permission_and_the_operation(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(grpc: true,
            services: s => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, DenyAll>()));
        var client = host.Client(binding);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest()));

        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
        var metadata = ErrorInfoMetadata(ex);
        Assert.Equal("operation", metadata["permission"]);
        Assert.Equal("ListTasks", metadata["operation"]);
        Assert.Contains("ListTasks", ex.Message);
    }

    /// <summary>
    ///     The ErrorInfo metadata as the client received it: the gRPC client puts it in <see cref="A2AException.Metadata" />,
    ///     the HTTP clients keep it in the ErrorInfo entry of <see cref="A2AException.Details" />.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ErrorInfoMetadata(A2AException ex)
    {
        if (ex.Metadata is { } metadata) return metadata;
        var info = Assert.Single(ex.Details!, d => d.GetProperty("@type").GetString() == A2AErrors.ErrorInfoType);
        return info.GetProperty("metadata").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }
}
