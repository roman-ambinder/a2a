using A2A.Server;
using A2A.Server.AspNetCore;

namespace A2A.Tests;

public class MediaTypeTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private static Message With(string? mediaType) => new()
    {
        MessageId = Message.NewId(), Role = Role.User,
        Parts = [Part.FromText("hi", mediaType)],
    };

    private static Task<TestAgentHost> Host(List<string>? defaults, List<string>? skillModes = null) =>
        TestAgentHost.StartAsync(o =>
        {
            var card = TestAgentHost.DefaultCard();
            card.DefaultInputModes = defaults ?? [];
            card.Skills[0].InputModes = skillModes;
            o.AgentCard = card;
        });

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task An_undeclared_media_type_is_rejected_on_send_and_stream(string binding)
    {
        await using var host = await Host(["text/plain"]);
        var client = host.Client(binding);
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(With("application/x-foo")));
        Assert.Equal(A2AErrorCode.ContentTypeNotSupported, ex.Code);
        Assert.Contains("application/x-foo", ex.Message);
        var stream = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(With("application/x-foo"))) { }
        });
        Assert.Equal(A2AErrorCode.ContentTypeNotSupported, stream.Code);
    }

    [Theory]
    [InlineData("text/plain", "text/plain")]
    [InlineData("text/*", "text/markdown")]
    [InlineData("*/*", "application/x-foo")]
    [InlineData("text/plain", "TEXT/Plain; charset=utf-8")]
    [InlineData(" Text/Plain ;q=1", "text/plain")]
    public async Task A_declared_media_type_is_accepted(string mode, string mediaType)
    {
        await using var host = await Host([mode]);
        var resp = await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(With(mediaType));
        Assert.NotNull(resp.RequireTask());
    }

    [Fact]
    public async Task A_media_type_declared_by_a_skill_is_accepted()
    {
        await using var host = await Host(["text/plain"], ["application/x-foo"]);
        Assert.NotNull((await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(With("application/x-foo"))).RequireTask());
    }

    [Fact]
    public async Task A_card_without_modes_accepts_everything()
    {
        await using var host = await Host(null);
        Assert.NotNull((await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(With("application/x-foo"))).RequireTask());
    }

    [Fact]
    public async Task A_part_without_a_media_type_is_never_rejected()
    {
        await using var host = await Host(["application/only-this"]);
        Assert.NotNull((await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(With(null))).RequireTask());
        Assert.NotNull((await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(With(""))).RequireTask());
    }

    [Fact]
    public async Task A_rejected_message_leaves_no_task()
    {
        await using var host = await Host(["text/plain"]);
        var client = host.Client(ProtocolBindings.JsonRpc);
        await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(With("application/x-foo")));
        Assert.Empty((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
    }
}
