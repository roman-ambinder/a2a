using System.Text.Json;
using A2A.Client;
using A2A.Server;
using A2A.Server.TypedSkills;
using A2A.Skills;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests.TypedSkills;

public class TypedSkillServerTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    private const string Canary = "CANARY-7731";

    /// <summary>Counts what ran. Shared by the handler and the fallback.</summary>
    private sealed class Probe
    {
        private int _handled, _fallbackRuns, _fallbackCancels;
        public int Handled => Volatile.Read(ref _handled);
        public int FallbackRuns => Volatile.Read(ref _fallbackRuns);
        public int FallbackCancels => Volatile.Read(ref _fallbackCancels);
        public void Handle() => Interlocked.Increment(ref _handled);
        public void FallbackRun() => Interlocked.Increment(ref _fallbackRuns);
        public void FallbackCancel() => Interlocked.Increment(ref _fallbackCancels);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BookingRequest? LastInput { get; set; }
        public string? LastTenant { get; set; }
    }

    private sealed class Fallback(Probe probe) : IAgentExecutor
    {
        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            probe.FallbackRun();
            if (context.UserText == "slow")
            {
                await updater.StartWorkAsync(cancellationToken: ct);
                await Task.Delay(Timeout.Infinite, ct);
            }
            await updater.CompleteAsync("fallback ran", ct);
        }

        public Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken ct)
        {
            probe.FallbackCancel();
            return updater.CancelAsync("fallback canceled", ct).AsTask();
        }
    }

    private static async Task<(TestAgentHost Host, Probe Probe)> StartAsync(bool fallback = true, bool secondSkill = false)
    {
        var probe = new Probe();
        var host = await TestAgentHost.StartAsync(services: services =>
        {
            services.AddSingleton(probe);
            services.AddA2AServer().AddTypedSkills(skills =>
            {
                skills.Map("book", TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult, async (context, ct) =>
                {
                    probe.Handle();
                    probe.LastInput = context.Input;
                    probe.LastTenant = context.Request.Call.Tenant;
                    if (context.Input.City == "fail") throw new TypedSkillFailedException("No rooms to be had.");
                    if (context.Input.City == "boom") throw new InvalidOperationException("internal secret");
                    if (context.Input.City == "slow")
                    {
                        probe.Started.TrySetResult();
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    await context.ReportProgressAsync("working on it", ct);
                    return new BookingResult { City = context.Input.City, Nights = context.Input.Nights, Reference = "ref-1" };
                }, o => o.Validate = request => request.Nights is < 0 or > 30
                    ? [new TypedSkillValidationError("$.nights", "Must be between 0 and 30.")]
                    : []);
                if (secondSkill) skills.Map("tree", TypedJson.Default.TreeNode, TypedJson.Default.TreeNode, (context, ct) => Task.FromResult(context.Input));
                if (fallback) skills.UseFallback<Fallback>();
            });
        });
        return (host, probe);
    }

    private static A2AClient ClientFor(TestAgentHost host, string binding) =>
        host.Client(binding, "alice", new A2AClientOptions { ValidateCapabilities = false });

    private static A2ARequestOptions Activated() => new() { Extensions = [SkillSelection.ExtensionUri] };

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Message DataMsg(string json, string? skill = "book", string? taskId = null)
    {
        var message = new Message { MessageId = Guid.NewGuid().ToString(), Role = Role.User, Parts = [Part.FromData(Json(json))], TaskId = taskId };
        return skill is null ? message : message.SetRequestedSkillId(skill);
    }

    private const string Good = """{"city":"Oslo","nights":2}""";

    private static async Task<A2AException> RefusedAsync(TestAgentHost host, Probe probe, string binding, Message message, bool streaming = false)
    {
        var client = ClientFor(host, binding);
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            if (streaming) await foreach (var _ in client.SendStreamingMessageAsync(message, options: Activated())) { }
            else await client.SendMessageAsync(message, options: Activated());
        });
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(0, probe.Handled);
        Assert.Equal(0, probe.FallbackRuns);
        Assert.Empty((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
        var text = ex.Message + string.Join("", ex.Details?.Select(d => d.GetRawText()) ?? []);
        Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BookingRequest", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Int32", text, StringComparison.Ordinal);
        return ex;
    }

    private static JsonElement BadRequest(A2AException ex) =>
        ex.Details!.Single(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("@type", out var t) && t.GetString() == "type.googleapis.com/google.rpc.BadRequest");

    private static string[] Fields(A2AException ex) =>
        BadRequest(ex).GetProperty("fieldViolations").EnumerateArray().Select(v => v.GetProperty("field").GetString()!).ToArray();

    // ---- D6: what is checked ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task Refuses_a_message_without_a_data_part(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var message = new Message { MessageId = "m1", Role = Role.User, Parts = [Part.FromText("book oslo")] }.SetRequestedSkillId("book");
        var ex = await RefusedAsync(host, probe, binding, message);
        Assert.Equal(["message.parts"], Fields(ex));
        Assert.Equal(TypedSkillExtension.ExtensionUri, MetadataOf(ex, "extension"));
    }

    private static string? MetadataOf(A2AException ex, string key)
    {
        var info = ex.Details!.First(d => d.TryGetProperty("@type", out var t) && t.GetString() == A2AErrors.ErrorInfoType);
        return info.GetProperty("metadata").GetProperty(key).GetString();
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Refuses_two_data_parts(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var message = DataMsg(Good);
        message.Parts.Add(Part.FromData(Json(Good)));
        await RefusedAsync(host, probe, binding, message);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Ignores_text_parts_next_to_one_data_part(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var message = DataMsg(Good);
        message.Parts.Add(Part.FromText("please"));
        await ClientFor(host, binding).SendMessageAsync(message, options: Activated());
        await host.WaitIdleAsync();
        Assert.Equal(1, probe.Handled);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Refuses_a_missing_required_property(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var ex = await RefusedAsync(host, probe, binding, DataMsg("""{"nights":2}"""));
        Assert.Equal(["$.city"], Fields(ex));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Refuses_a_wrong_type_without_echoing_the_value(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var ex = await RefusedAsync(host, probe, binding, DataMsg($$"""{"city":"Oslo","nights":"{{Canary}}"}"""));
        Assert.Equal(["$.nights"], Fields(ex));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Refuses_a_bad_dictionary_value_without_echoing_the_key(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var ex = await RefusedAsync(host, probe, binding, DataMsg($"{{\"city\":\"Oslo\",\"nights\":1,\"counts\":{{\"{Canary}\":\"x\"}}}}"));
        Assert.Equal(["$.counts[*]"], Fields(ex));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Refuses_data_that_is_not_an_object(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        await RefusedAsync(host, probe, binding, DataMsg($"\"{Canary}\""));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Refuses_null_data(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        await RefusedAsync(host, probe, binding, DataMsg("null"));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Refuses_input_the_validator_rejects(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var ex = await RefusedAsync(host, probe, binding, DataMsg("""{"city":"Oslo","nights":99}"""));
        Assert.Equal(["$.nights"], Fields(ex));
        Assert.Equal("Must be between 0 and 30.", BadRequest(ex).GetProperty("fieldViolations")[0].GetProperty("description").GetString());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Refuses_bad_input_on_a_streaming_send(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        await RefusedAsync(host, probe, binding, DataMsg("""{"nights":2}"""), streaming: true);
    }

    // ---- D7: routing ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task Row1_names_a_typed_skill_goes_to_the_handler(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        await ClientFor(host, binding).SendMessageAsync(DataMsg(Good), options: Activated());
        await host.WaitIdleAsync();
        Assert.Equal(1, probe.Handled);
        Assert.Equal(0, probe.FallbackRuns);
        Assert.Equal("Oslo", probe.LastInput!.City);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Row2_names_a_typed_skill_with_a_task_id_is_refused(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        await RefusedAsync(host, probe, binding, DataMsg(Good, taskId: "t-1"));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Row3_names_another_skill_goes_to_the_fallback(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var message = Message.FromUser("hello").SetRequestedSkillId("other");
        await ClientFor(host, binding).SendMessageAsync(message, options: Activated());
        await host.WaitIdleAsync();
        Assert.Equal(1, probe.FallbackRuns);
        Assert.Equal(0, probe.Handled);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Row3_names_another_skill_without_a_fallback_is_refused_without_repeating_the_id(string binding)
    {
        var (host, probe) = await StartAsync(fallback: false);
        await using var disposeHost = host;
        var ex = await RefusedAsync(host, probe, binding, DataMsg(Good, skill: Canary));
        Assert.DoesNotContain("book", ex.Message, StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Row4_names_nothing_with_one_typed_skill_and_no_fallback_goes_to_that_skill(string binding)
    {
        var (host, probe) = await StartAsync(fallback: false);
        await using var disposeHost = host;
        var result = await ClientFor(host, binding).SendMessageAsync(DataMsg(Good, skill: null));
        Assert.IsType<TaskResponse>(result);
        await host.WaitIdleAsync();
        Assert.Equal(1, probe.Handled);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Row5_names_nothing_with_a_fallback_goes_to_the_fallback(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        await ClientFor(host, binding).SendMessageAsync(DataMsg(Good, skill: null));
        await host.WaitIdleAsync();
        Assert.Equal(1, probe.FallbackRuns);
        Assert.Equal(0, probe.Handled);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Row5_names_nothing_with_two_typed_skills_and_no_fallback_is_refused(string binding)
    {
        var (host, probe) = await StartAsync(fallback: false, secondSkill: true);
        await using var disposeHost = host;
        await RefusedAsync(host, probe, binding, DataMsg(Good, skill: null));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Row5_names_nothing_with_a_task_id_and_no_fallback_is_refused(string binding)
    {
        var (host, probe) = await StartAsync(fallback: false);
        await using var disposeHost = host;
        await RefusedAsync(host, probe, binding, DataMsg(Good, skill: null, taskId: "t-1"));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_skill_id_without_the_extension_does_not_route(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        await ClientFor(host, binding).SendMessageAsync(DataMsg(Good)); // key in metadata, extension not requested
        await host.WaitIdleAsync();
        Assert.Equal(0, probe.Handled);
        Assert.Equal(1, probe.FallbackRuns);
    }

    // ---- success, streaming, failure ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task CallSkillAsync_returns_the_typed_result_and_stores_one_data_artifact(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var client = ClientFor(host, binding);
        var result = await client.CallSkillAsync("book", new BookingRequest { City = "Oslo", Nights = 3 },
            TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult);
        Assert.Equal(("Oslo", 3, "ref-1"), (result.City, result.Nights, result.Reference));
        Assert.Equal(1, probe.Handled);

        var task = Assert.Single((await client.ListTasksAsync(new ListTasksRequest { IncludeArtifacts = true })).Tasks);
        Assert.Equal(TaskState.Completed, task.Status.State);
        var artifact = Assert.Single(task.Artifacts!);
        Assert.Equal("book", artifact.Name);
        Assert.IsType<DataPart>(Assert.Single(artifact.Parts));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task CallSkillAsync_sends_input_the_server_refuses_as_InvalidParams(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var ex = await Assert.ThrowsAsync<A2AException>(() => ClientFor(host, binding).CallSkillAsync("book",
            new BookingRequest { City = "Oslo", Nights = 99 }, TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(["$.nights"], Fields(ex));
        Assert.Equal(0, probe.Handled);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_streaming_send_reaches_the_handler_and_shows_progress(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var events = new List<StreamResponse>();
        await foreach (var e in ClientFor(host, binding).SendStreamingMessageAsync(DataMsg(Good), options: Activated())) events.Add(e);
        Assert.Equal(1, probe.Handled);
        Assert.Contains(events, e => e is StatusUpdateEvent { StatusUpdate.Status.Message: { } m } && m.GetText() == "working on it");
        Assert.Contains(events, e => e is ArtifactUpdateEvent { ArtifactUpdate.Artifact: { Name: "book" } });
        Assert.Contains(events, e => e is StatusUpdateEvent { StatusUpdate.Status.State: TaskState.Completed });
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task TypedSkillFailedException_fails_the_task_with_its_message(string binding)
    {
        var (host, _) = await StartAsync();
        await using var disposeHost = host;
        var ex = await Assert.ThrowsAsync<TypedSkillCallException>(() => ClientFor(host, binding).CallSkillAsync("book",
            new BookingRequest { City = "fail", Nights = 1 }, TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult));
        Assert.Equal(TaskState.Failed, ex.State);
        Assert.Equal("No rooms to be had.", ex.Task.Status.Message!.GetText());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Another_exception_fails_the_task_without_its_text(string binding)
    {
        var (host, _) = await StartAsync();
        await using var disposeHost = host;
        var ex = await Assert.ThrowsAsync<TypedSkillCallException>(() => ClientFor(host, binding).CallSkillAsync("book",
            new BookingRequest { City = "boom", Nights = 1 }, TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult));
        Assert.Equal(TaskState.Failed, ex.State);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_handler_sees_the_tenant_and_the_scope(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        await ClientFor(host, binding).CallSkillAsync("book", new BookingRequest { City = "Oslo", Nights = 1 },
            TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult);
        Assert.Equal(string.Empty, probe.LastTenant);
    }

    // ---- fallback and cancel (D9) ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_fallback_executor_still_runs_plain_messages(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var response = await ClientFor(host, binding).SendMessageAsync("hello");
        Assert.Contains("fallback ran", response.RequireTask().Status.Message!.GetText());
        Assert.Equal(1, probe.FallbackRuns);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Cancel_of_a_fallback_task_goes_to_the_fallback(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var client = ClientFor(host, binding);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        var canceled = await client.CancelTaskAsync(task.Id);
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        Assert.Equal(1, probe.FallbackCancels); // the status text is not checked: the stopped run can cancel the task first
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Cancel_of_a_typed_task_uses_the_default_cancel(string binding)
    {
        var (host, probe) = await StartAsync();
        await using var disposeHost = host;
        var client = ClientFor(host, binding);
        var task = (await client.SendMessageAsync(DataMsg("""{"city":"slow","nights":1}"""), new SendMessageConfiguration { ReturnImmediately = true }, Activated())).RequireTask();
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var canceled = await client.CancelTaskAsync(task.Id);
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        Assert.Null(canceled.Status.Message);
        Assert.Equal(0, probe.FallbackCancels);
    }

    // ---- registration ----

    [Fact]
    public async Task The_card_of_the_host_lists_the_skill_and_its_schemas()
    {
        var (host, _) = await StartAsync();
        await using var disposeHost = host;
        var card = host.Handler.AgentCard;
        Assert.Contains(card.Skills, s => s.Id == "book");
        Assert.True(TypedSkillExtension.TryGetSchemas(card, "book", out _, out _));
    }

    [Fact]
    public async Task AddAgentCard_after_AddTypedSkills_still_gets_the_skills()
    {
        await using var host = await TestAgentHost.StartAsync(services: services =>
        {
            var card = TestAgentHost.DefaultCard();
            card.Skills.Clear();
            services.AddA2AServer()
                .AddTypedSkills(s => s.Map("tree", TypedJson.Default.TreeNode, TypedJson.Default.TreeNode, (c, ct) => Task.FromResult(c.Input)))
                .AddAgentCard(card);
        });
        var card = host.Handler.AgentCard;
        Assert.Equal(["tree"], card.Skills.Select(s => s.Id));
        Assert.True(TypedSkillExtension.TryGetSchemas(card, "tree", out _, out _));
    }

    [Fact]
    public void AddTypedSkills_twice_or_with_no_skill_throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddA2AServer();
        Assert.Throws<InvalidOperationException>(() => builder.AddTypedSkills(_ => { }));
        builder.AddTypedSkills(s => s.Map("tree", TypedJson.Default.TreeNode, TypedJson.Default.TreeNode, (c, ct) => Task.FromResult(c.Input)));
        Assert.Throws<InvalidOperationException>(() => builder.AddTypedSkills(s => s.Map("tree", TypedJson.Default.TreeNode, TypedJson.Default.TreeNode, (c, ct) => Task.FromResult(c.Input))));
    }
}
