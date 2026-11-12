using System.Text;
using System.Text.Json;
using A2A.Json;
using A2A.Server;
using A2A.Server.Storage;

namespace A2A.Tests.Storage;

/// <summary>
///     The size the context history counts per message is the UTF-8 length of its JSON, measured without building a
///     string; and the in-memory ListTasks pages in the documented order whatever the page size.
/// </summary>
public sealed class StoreMeasureAndPagingTests
{
    private static long AsString(Message m) => Encoding.UTF8.GetByteCount(A2AJson.Serialize(m));

    public static TheoryData<string> Texts() =>
    [
        "",
        "plain ascii",
        "quotes \" and \\ backslash, tab\t newline\n and control \u0001",
        "héllo wörld ✓ 日本語",
        "emoji 🎉👩‍👩‍👧",
        "lone high \ud800 and lone low \udc00 surrogates",
        "<script>&amp;</script> '+'",
        new string('x', 100_000) + "é",
    ];

    [Theory]
    [MemberData(nameof(Texts))]
    public void Measured_length_is_the_utf8_length_of_the_serialized_string(string text)
    {
        var message = new Message
        {
            MessageId = "m-é", Role = Role.Agent, ContextId = "ctx", TaskId = "t",
            Parts =
            [
                Part.FromText(text),
                Part.FromBytes(new byte[] { 0, 1, 2, 250 }, "application/octet-stream", "f ✓.bin"),
                Part.FromUrl(new Uri("https://example.com/ä?q=1"), "text/plain"),
                Part.FromData(JsonDocument.Parse("{\"k\":\"vé\",\"n\":[1,2.5,null,true]}").RootElement),
            ],
            Metadata = new Dictionary<string, JsonElement> { ["kü"] = JsonSerializer.SerializeToElement(text) },
        };

        Assert.Equal(AsString(message), Utf8Length.Of(message, A2AJson.GetTypeInfo<Message>()));
    }

    [Fact]
    public async Task Context_history_cuts_at_the_same_byte_count_as_before()
    {
        var store = new InMemoryTaskStore(new InMemoryEventStore(new StreamingOptions()));
        var history = new List<Message>();
        for (var i = 0; i < 5; i++)
            history.Add(new Message
            {
                MessageId = "m" + i, Role = i % 2 == 0 ? Role.User : Role.Agent, ContextId = "ctx",
                Parts = [Part.FromText("message ✓ " + new string('é', i * 3))],
            });
        var task = new AgentTask
        {
            Id = "t1", ContextId = "ctx", Status = new AgentTaskStatus { State = TaskState.Completed }, History = history,
        };
        var now = DateTimeOffset.UtcNow;
        await store.CreateAsync(new TaskRecord("t", "alice", task, 0, now, now));

        // The three newest messages fit exactly; one byte less and only two do.
        var three = history.Skip(2).Sum(AsString);
        var fit = await store.ReadContextMessagesAsync(new ContextHistoryQuery("t", "alice", "ctx", 10, three, null));
        Assert.Equal(["m2", "m3", "m4"], fit.Select(m => m.MessageId));
        var less = await store.ReadContextMessagesAsync(new ContextHistoryQuery("t", "alice", "ctx", 10, three - 1, null));
        Assert.Equal(["m3", "m4"], less.Select(m => m.MessageId));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(50)]
    public async Task List_pages_cover_every_match_in_order_with_ties_on_time(int pageSize)
    {
        var store = new InMemoryTaskStore(new InMemoryEventStore(new StreamingOptions()));
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expected = new List<(long Ticks, string Id)>();
        for (var i = 0; i < 23; i++)
        {
            var time = baseTime.AddSeconds(i % 4); // four timestamps: many ties, broken by id
            var id = $"task-{(i * 7) % 23:D2}";
            var state = i % 5 == 0 ? TaskState.Failed : TaskState.Completed;
            var task = new AgentTask
            {
                Id = id, ContextId = "ctx", Status = new AgentTaskStatus { State = state, Timestamp = time },
            };
            await store.CreateAsync(new TaskRecord("t", "alice", task, 0, time, time));
            if (state == TaskState.Completed) expected.Add((time.UtcTicks, id));
        }

        // Another owner's and another tenant's tasks never show.
        var other = new AgentTask { Id = "other", ContextId = "ctx", Status = new AgentTaskStatus { State = TaskState.Completed } };
        await store.CreateAsync(new TaskRecord("t", "bob", other, 0, baseTime, baseTime));
        await store.CreateAsync(new TaskRecord("u", "alice", other, 0, baseTime, baseTime));

        var ordered = expected.OrderByDescending(e => e.Ticks).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => e.Id).ToList();
        var seen = new List<string>();
        string? token = null;
        do
        {
            var page = await store.ListAsync(new TaskQuery("t", "alice", null, TaskState.Completed, null, pageSize, token));
            Assert.Equal(ordered.Count, page.TotalSize);
            Assert.True(page.Records.Count <= pageSize);
            seen.AddRange(page.Records.Select(r => r.Task.Id));
            token = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        } while (token is not null);

        Assert.Equal(ordered, seen);
    }
}
