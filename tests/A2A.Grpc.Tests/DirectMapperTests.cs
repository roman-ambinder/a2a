using System.Text.Json;
using A2A.Json;
using Google.Protobuf.WellKnownTypes;
using A2aPb = Lf.A2a.V1;

namespace A2A.Grpc.Tests;

/// <summary>
/// The direct mapping of stream events and SendMessage responses must give exactly what the ProtoJSON path gives. Random models
/// and protos (fixed seed) go through both: where the direct mapping answers, the result must be equal; where the ProtoJSON
/// path fails, the direct mapping must not answer (it leaves every error to that path).
/// </summary>
public sealed class DirectMapperTests
{
    private const int Cases = 3000;

    [Fact]
    public void Model_to_proto_equals_the_ProtoJSON_path()
    {
        var rng = new Random(20261109);
        var answered = 0;
        for (var i = 0; i < Cases; i++)
        {
            var model = Gen.StreamResponse(rng);
            var direct = DirectMapper.ToProto(model);
            A2aPb.StreamResponse? reference = null;
            Exception? referenceError = null;
            try { reference = ProtoMapper.ToProtoViaJson<A2aPb.StreamResponse, StreamResponse>(model); }
            catch (Exception ex) { referenceError = ex; }

            if (direct is null) continue;
            answered++;
            Assert.True(referenceError is null, $"case {i}: the ProtoJSON path fails ({referenceError?.Message}) but the direct mapping answered.\n{Describe(model)}");
            Assert.True(reference!.Equals(direct), $"case {i}:\nexpected {reference}\nactual   {direct}\n{Describe(model)}");
        }
        Assert.True(answered > Cases * 0.6, $"the direct mapping answered only {answered} of {Cases}");
    }

    [Fact]
    public void Proto_to_model_equals_the_ProtoJSON_path()
    {
        var rng = new Random(9112026);
        var answered = 0;
        for (var i = 0; i < Cases; i++)
        {
            var proto = Gen.StreamResponseProto(rng);
            var direct = DirectMapper.ToModel(proto);
            StreamResponse? reference = null;
            Exception? referenceError = null;
            try { reference = ProtoMapper.ToModelViaJson<StreamResponse>(proto); }
            catch (Exception ex) { referenceError = ex; }

            if (direct is null) continue;
            answered++;
            Assert.True(referenceError is null, $"case {i}: the ProtoJSON path fails ({referenceError?.Message}) but the direct mapping answered.\n{proto}");
            Assert.Equal(Describe(reference!), Describe(direct));
        }
        Assert.True(answered > Cases * 0.4, $"the direct mapping answered only {answered} of {Cases}");
    }

    [Fact]
    public void SendMessageResponse_maps_both_ways_like_the_ProtoJSON_path()
    {
        var rng = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            SendMessageResponse model = rng.Next(2) == 0 ? new TaskResponse(Gen.Task(rng)) : new MessageResponse(Gen.Message(rng));
            A2aPb.SendMessageResponse proto;
            try { proto = ProtoMapper.ToProtoViaJson<A2aPb.SendMessageResponse, SendMessageResponse>(model); }
            catch (A2AException)
            {
                // The ProtoJSON path refuses it (a timeline entry with both kinds, say): the direct mapping must not answer.
                Assert.Null(DirectMapper.ToProto(model));
                continue;
            }

            if (DirectMapper.ToProto(model) is { } direct)
                Assert.Equal(proto, direct);

            if (DirectMapper.ToModel(proto) is { } back)
                Assert.Equal(Describe(ProtoMapper.ToModelViaJson<SendMessageResponse>(proto)), Describe(back));
        }
    }

    [Fact]
    public void SendMessageRequest_model_to_proto_equals_the_ProtoJSON_path()
    {
        var rng = new Random(1112026);
        var answered = 0;
        for (var i = 0; i < Cases; i++)
        {
            var model = Gen.SendMessageRequest(rng);
            var direct = DirectMapper.ToProto(model);
            A2aPb.SendMessageRequest? reference = null;
            Exception? referenceError = null;
            try { reference = ProtoMapper.ToProtoViaJson<A2aPb.SendMessageRequest, SendMessageRequest>(model); }
            catch (Exception ex) { referenceError = ex; }

            if (direct is null) continue;
            answered++;
            Assert.True(referenceError is null, $"case {i}: the ProtoJSON path fails ({referenceError?.Message}) but the direct mapping answered.\n{Describe(model)}");
            Assert.True(reference!.Equals(direct), $"case {i}:\nexpected {reference}\nactual   {direct}\n{Describe(model)}");
        }
        Assert.True(answered > Cases * 0.4, $"the direct mapping answered only {answered} of {Cases}");

        // A representative request, through the public mapper.
        var request = new SendMessageRequest
        {
            Tenant = "acme", Message = Representative.Message("m-1", Role.User), Metadata = Representative.Metadata(),
            Configuration = new SendMessageConfiguration
            {
                AcceptedOutputModes = ["text/plain"], HistoryLength = 3, TimelineLength = 0, ReturnImmediately = true, IfGenerationMatch = 12,
                TaskPushNotificationConfig = new TaskPushNotificationConfig
                {
                    Url = "https://hook", Token = "tok", Authentication = new AuthenticationInfo { Credentials = "c" }, ProtocolVersion = "1.1",
                },
            },
            ArtifactUpdates = [new TaskArtifactUpdateEvent { TaskId = "task-1", Artifact = new Artifact { ArtifactId = "in", Parts = Representative.Parts(), Role = Role.User } }],
        };
        Assert.NotNull(DirectMapper.ToProto(request));
        var proto = ProtoMapper.ToProto<A2aPb.SendMessageRequest, SendMessageRequest>(request);
        Assert.Equal(ProtoMapper.ToProtoViaJson<A2aPb.SendMessageRequest, SendMessageRequest>(request), proto);
        Assert.True(proto.Configuration.HasTimelineLength);
    }

    [Fact]
    public void The_public_mapper_uses_the_direct_path_and_gives_the_same_result()
    {
        StreamResponse evt = new ArtifactUpdateEvent(new TaskArtifactUpdateEvent
        {
            TaskId = "t", ContextId = "c", Append = true,
            Artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText("chunk")] },
        });
        Assert.NotNull(DirectMapper.ToProto(evt));
        var proto = ProtoMapper.ToProto<A2aPb.StreamResponse, StreamResponse>(evt);
        Assert.Equal(ProtoMapper.ToProtoViaJson<A2aPb.StreamResponse, StreamResponse>(evt), proto);
        Assert.Equal(Describe(evt), Describe(ProtoMapper.ToModel<StreamResponse>(proto)));
    }

    [Fact]
    public void A_task_with_a_timeline_maps_directly_both_ways_like_the_ProtoJSON_path()
    {
        var task = Representative.Task();
        foreach (var evt in new StreamResponse[]
                 {
                     new TaskEvent(task),
                     new StatusUpdateEvent(new TaskStatusUpdateEvent
                     {
                         TaskId = task.Id, ContextId = task.ContextId, Status = task.Status, Metadata = Representative.Metadata(), Generation = 7,
                     }),
                     new ArtifactUpdateEvent(new TaskArtifactUpdateEvent
                     {
                         TaskId = task.Id, ContextId = task.ContextId, Artifact = task.Artifacts![0], Append = true, LastChunk = true,
                         Metadata = Representative.Metadata(), Generation = 8,
                     }),
                     new MessageEvent(Representative.Message("m-solo", Role.Agent)),
                 })
        {
            var reference = ProtoMapper.ToProtoViaJson<A2aPb.StreamResponse, StreamResponse>(evt);
            var direct = DirectMapper.ToProto(evt);
            Assert.NotNull(direct);
            Assert.Equal(reference, direct);

            var back = DirectMapper.ToModel(reference);
            Assert.NotNull(back);
            Assert.Equal(Describe(ProtoMapper.ToModelViaJson<StreamResponse>(reference)), Describe(back));
        }

        SendMessageResponse response = new TaskResponse(task);
        var sendReference = ProtoMapper.ToProtoViaJson<A2aPb.SendMessageResponse, SendMessageResponse>(response);
        Assert.Equal(sendReference, DirectMapper.ToProto(response));
        Assert.Equal(task.Timeline!.Count, sendReference.Task.Timeline.Count);
        var sendBack = DirectMapper.ToModel(sendReference);
        Assert.NotNull(sendBack);
        Assert.Equal(Describe(ProtoMapper.ToModelViaJson<SendMessageResponse>(sendReference)), Describe(sendBack));
    }

    [Fact]
    public void Timeline_entries_the_direct_mapping_leaves_to_the_ProtoJSON_path()
    {
        var task = Representative.Task();
        // Both kinds set: two members of the oneof.
        task.Timeline!.Add(new TimelineEntry { Message = Representative.Message("m-x", Role.User), Status = new AgentTaskStatus(), Generation = 9 });
        Assert.Null(DirectMapper.ToProto(new TaskEvent(task)));

        task.Timeline[^1] = null!;
        Assert.Null(DirectMapper.ToProto(new TaskEvent(task)));

        // An entry whose message the model cannot hold (no parts).
        var proto = ProtoMapper.ToProtoViaJson<A2aPb.StreamResponse, StreamResponse>(new TaskEvent(Representative.Task()));
        proto.Task.Timeline.Add(new A2aPb.TimelineEntry { Message = new A2aPb.Message { MessageId = "m", Role = A2aPb.Role.User }, Generation = 10 });
        Assert.Null(DirectMapper.ToModel(proto));
        Assert.Throws<A2AException>(() => ProtoMapper.ToModelViaJson<StreamResponse>(proto));
    }

    private static string Describe<T>(T value) => A2AJson.Serialize(value);

    /// <summary>A task as a server returns it: a timeline of client messages and agent statuses, every part kind, metadata.</summary>
    private static class Representative
    {
        private static JsonElement J(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        public static Dictionary<string, JsonElement> Metadata() => new()
        {
            ["trace"] = J("\"abc\""),
            ["n"] = J("1.5"),
            ["nested"] = J("{\"a\":[1,true,null,\"x\"],\"b\":{}}"),
        };

        public static List<Part> Parts() =>
        [
            new TextPart("hello é 中文") { Metadata = Metadata() },
            new RawPart(new byte[] { 0, 1, 2, 250 }) { Filename = "a.bin", MediaType = "application/octet-stream" },
            new UrlPart(new Uri("https://example.com/f.png")) { Filename = "f.png", MediaType = "image/png" },
            new DataPart(J("{\"k\":[1,2.5,\"s\"],\"o\":{\"z\":null}}")) { Metadata = Metadata() },
        ];

        public static Message Message(string id, Role role) => new()
        {
            MessageId = id, ContextId = "ctx-1", TaskId = "task-1", Role = role, Parts = Parts(), Metadata = Metadata(),
            Extensions = ["https://ext/a"], ReferenceTaskIds = ["task-0"],
        };

        private static AgentTaskStatus Status(TaskState state, int minute, Message? message) => new()
        {
            State = state, Message = message, Timestamp = new DateTimeOffset(2026, 11, 12, 10, minute, 3, 456, TimeSpan.Zero),
        };

        public static AgentTask Task()
        {
            var timeline = new List<TimelineEntry> { TimelineEntry.FromMessage(Message("m-1", Role.User), 1) };
            for (var i = 0; i < 20; i++)
                timeline.Add(TimelineEntry.FromStatus(Status(TaskState.Working, i, i % 3 == 0 ? Message("s-" + i, Role.Agent) : null), 2 + i * 2));
            timeline.Add(new TimelineEntry { Generation = 60 }); // a kind this SDK does not know
            timeline.Add(TimelineEntry.FromMessage(Message("m-2", Role.User), 61));
            return new AgentTask
            {
                Id = "task-1", ContextId = "ctx-1",
                Status = Status(TaskState.InputRequired, 59, Message("s-last", Role.Agent)),
                Artifacts =
                [
                    new Artifact
                    {
                        ArtifactId = "art-1", Name = "report", Description = "d", Parts = Parts(), Metadata = Metadata(),
                        Extensions = ["https://ext/a"], StartGeneration = 3, EndGeneration = 5, Role = Role.Agent,
                    },
                ],
                History = [Message("m-1", Role.User), Message("m-2", Role.User)],
                Metadata = Metadata(),
                Generation = 62,
                Timeline = timeline,
            };
        }
    }

    /// <summary>Random models and protos, biased toward edge values: empty strings, nulls, odd numbers, sub-millisecond times.</summary>
    private static class Gen
    {
        private static readonly string[] Strings = ["", "a", "task-1", "é ü 中文", "line\nbreak", "quote\"back\\slash", "<tag>&amp;", "\u2028", "\uD83D\uDE00"];

        private static readonly string[] Numbers =
            ["0", "1", "-1", "0.1", "1.5", "1e21", "1E-7", "123456789012345678", "9007199254740993", "-0", "3.14159265358979", "1.0", "100"];

        private static readonly string[] Urls = ["https://x/y.png", "file:///tmp/a", "relative/path", "", "http://[::1]:80/", "mailto:a@b.c"];

        private static T Pick<T>(Random r, T[] values) => values[r.Next(values.Length)];

        private static string? MaybeString(Random r) => r.Next(4) == 0 ? null : Pick(r, Strings);

        public static StreamResponse StreamResponse(Random r) => r.Next(4) switch
        {
            0 => new TaskEvent(Task(r)),
            1 => new MessageEvent(Message(r)),
            2 => new StatusUpdateEvent(new TaskStatusUpdateEvent
            {
                TaskId = Pick(r, Strings), ContextId = Pick(r, Strings), Status = Status(r), Metadata = Metadata(r), Generation = Generation(r),
            }),
            _ => new ArtifactUpdateEvent(new TaskArtifactUpdateEvent
            {
                TaskId = Pick(r, Strings), ContextId = Pick(r, Strings), Artifact = Artifact(r), Append = r.Next(2) == 0,
                LastChunk = r.Next(2) == 0, Metadata = Metadata(r), Generation = Generation(r),
            }),
        };

        public static AgentTask Task(Random r) => new()
        {
            Id = r.Next(10) == 0 ? "" : "task-" + r.Next(100),
            ContextId = Pick(r, Strings),
            Status = Status(r),
            Artifacts = r.Next(3) == 0 ? null : Enumerable.Range(0, r.Next(3)).Select(_ => Artifact(r)).ToList(),
            History = r.Next(3) == 0 ? null : Enumerable.Range(0, r.Next(3)).Select(_ => Message(r)).ToList(),
            Metadata = Metadata(r),
            Generation = Generation(r),
            Timeline = r.Next(4) == 0 ? null : Enumerable.Range(0, r.Next(5)).Select(_ => TimelineEntry(r)).ToList(),
        };

        private static TimelineEntry TimelineEntry(Random r)
        {
            var generation = r.Next(4) switch { 0 => 0, 1 => -r.Next(1, 10), _ => r.NextInt64(1, long.MaxValue) };
            return r.Next(20) switch
            {
                0 => null!, // a null item: the text path decides
                1 => new TimelineEntry { Generation = generation }, // a kind this SDK does not know
                2 => new TimelineEntry { Message = Message(r), Status = Status(r), Generation = generation }, // both: refused
                < 10 => A2A.TimelineEntry.FromMessage(Message(r), generation),
                _ => A2A.TimelineEntry.FromStatus(Status(r), generation),
            };
        }

        private static AgentTaskStatus Status(Random r) => new()
        {
            State = r.Next(12) == 0 ? (TaskState)42 : (TaskState)r.Next(9),
            Message = r.Next(3) == 0 ? Message(r) : null,
            Timestamp = r.Next(3) == 0 ? null : new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(r.Next(-5, 6)))
                .AddTicks(r.NextInt64(0, TimeSpan.TicksPerDay * 400)),
        };

        public static Message Message(Random r) => new()
        {
            MessageId = r.Next(10) == 0 ? "" : "m-" + r.Next(1000),
            ContextId = MaybeString(r),
            TaskId = MaybeString(r),
            Role = r.Next(10) == 0 ? (Role)r.Next(0, 5) : (Role)r.Next(1, 3),
            Parts = Enumerable.Range(0, r.Next(10) == 0 ? 0 : r.Next(1, 4)).Select(_ => Part(r)).ToList(),
            Metadata = Metadata(r),
            Extensions = r.Next(3) == 0 ? ["https://ext/a", Pick(r, Strings)] : null,
            ReferenceTaskIds = r.Next(4) == 0 ? ["t1"] : null,
        };

        private static Artifact Artifact(Random r) => new()
        {
            ArtifactId = r.Next(10) == 0 ? "" : "art-" + r.Next(10),
            Name = MaybeString(r),
            Description = MaybeString(r),
            Parts = Enumerable.Range(0, r.Next(4)).Select(_ => Part(r)).ToList(),
            Metadata = Metadata(r),
            Extensions = r.Next(4) == 0 ? ["x"] : null,
            StartGeneration = Generation(r),
            EndGeneration = Generation(r),
            Role = r.Next(3) == 0 ? null : (Role)r.Next(0, 4),
        };

        private static Part Part(Random r)
        {
            Part part = r.Next(5) switch
            {
                0 or 1 => new TextPart(Pick(r, Strings)),
                2 => new RawPart(Enumerable.Range(0, r.Next(5)).Select(_ => (byte)r.Next(256)).ToArray()),
                3 => Uri.TryCreate(Pick(r, Urls), UriKind.RelativeOrAbsolute, out var uri) ? new UrlPart(uri) : new TextPart("x"),
                _ => new DataPart(Json(r, 0)),
            };
            return part with { Metadata = Metadata(r), Filename = MaybeString(r), MediaType = r.Next(2) == 0 ? null : "text/plain" };
        }

        public static SendMessageRequest SendMessageRequest(Random r) => new()
        {
            Tenant = MaybeString(r),
            Message = r.Next(5) == 0 ? null : Message(r),
            Metadata = Metadata(r),
            Configuration = r.Next(3) == 0 ? null : new SendMessageConfiguration
            {
                AcceptedOutputModes = r.Next(3) == 0 ? null : r.Next(10) == 0 ? ["a", null!] : ["text/plain", Pick(r, Strings)],
                HistoryLength = r.Next(3) switch { 0 => null, 1 => 0, _ => r.Next(-5, 100) },
                TimelineLength = r.Next(3) switch { 0 => null, 1 => 0, _ => r.Next(-5, 100) },
                ReturnImmediately = r.Next(2) == 0,
                IfGenerationMatch = Generation(r),
                TaskPushNotificationConfig = r.Next(3) == 0 ? null : new TaskPushNotificationConfig
                {
                    Tenant = MaybeString(r), Id = MaybeString(r), TaskId = MaybeString(r), Url = r.Next(10) == 0 ? null! : Pick(r, Strings),
                    Token = MaybeString(r), ProtocolVersion = MaybeString(r),
                    Authentication = r.Next(3) == 0 ? null : new AuthenticationInfo { Scheme = r.Next(5) == 0 ? null! : Pick(r, Strings), Credentials = MaybeString(r) },
                },
            },
            ArtifactUpdates = r.Next(3) == 0 ? null : Enumerable.Range(0, r.Next(3)).Select(_ => r.Next(15) == 0 ? null! : new TaskArtifactUpdateEvent
            {
                TaskId = Pick(r, Strings), ContextId = Pick(r, Strings), Artifact = Artifact(r), Append = r.Next(2) == 0,
                LastChunk = r.Next(2) == 0, Metadata = Metadata(r), Generation = Generation(r),
            }).ToList(),
        };

        private static long? Generation(Random r) => r.Next(4) switch { 0 => null, 1 => 0, 2 => r.NextInt64(1, long.MaxValue), _ => -r.Next(1, 10) };

        private static Dictionary<string, JsonElement>? Metadata(Random r)
        {
            if (r.Next(3) != 0) return null;
            var d = new Dictionary<string, JsonElement>();
            for (var i = r.Next(4); i > 0; i--) d[Pick(r, Strings) + i] = Json(r, 0);
            return d;
        }

        private static JsonElement Json(Random r, int depth)
        {
            var text = (depth > 2 ? r.Next(4) : r.Next(7)) switch
            {
                0 => Pick(r, Numbers),
                1 => JsonSerializer.Serialize(Pick(r, Strings)),
                2 => r.Next(2) == 0 ? "true" : "false",
                3 => "null",
                4 => "[" + string.Join(",", Enumerable.Range(0, r.Next(3)).Select(_ => Json(r, depth + 1).GetRawText())) + "]",
                _ => "{" + string.Join(",", Enumerable.Range(0, r.Next(3)).Select(i => $"\"k{i}\":" + Json(r, depth + 1).GetRawText())) + "}",
            };
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        /// <summary>A proto built from a random model, then changed at random in ways a peer could send.</summary>
        public static A2aPb.StreamResponse StreamResponseProto(Random r)
        {
            A2aPb.StreamResponse proto;
            try { proto = ProtoMapper.ToProtoViaJson<A2aPb.StreamResponse, StreamResponse>(StreamResponse(r)); }
            catch (A2AException) { proto = new A2aPb.StreamResponse(); }
            switch (r.Next(8))
            {
                case 0 when proto.PayloadCase == A2aPb.StreamResponse.PayloadOneofCase.StatusUpdate && proto.StatusUpdate.Status is { } s:
                    s.Timestamp = new Timestamp { Seconds = 1_800_000_000, Nanos = r.Next(1_000_000_000) };
                    break;
                case 1 when proto.PayloadCase == A2aPb.StreamResponse.PayloadOneofCase.Task:
                    proto.Task.Timeline.Add(new A2aPb.TimelineEntry { Generation = 1 });
                    break;
                case 2:
                    proto.MessageUpdate = new A2aPb.TaskMessageUpdateEvent { TaskId = "t" };
                    break;
                case 3 when proto.PayloadCase == A2aPb.StreamResponse.PayloadOneofCase.Message:
                    proto.Message.Parts.Add(new A2aPb.Part { Data = Value.ForNull() });
                    break;
                case 4 when proto.PayloadCase == A2aPb.StreamResponse.PayloadOneofCase.Message:
                    proto.Message.Parts.Add(new A2aPb.Part { Data = Value.ForNumber(double.NaN), Metadata = new Struct { Fields = { ["n"] = Value.ForNumber(1e300) } } });
                    break;
                case 5 when proto.PayloadCase == A2aPb.StreamResponse.PayloadOneofCase.ArtifactUpdate && proto.ArtifactUpdate.Artifact is { } a:
                    a.Parts.Add(new A2aPb.Part()); // no content
                    a.Role = (A2aPb.Role)7;
                    break;
            }
            return proto;
        }
    }
}
