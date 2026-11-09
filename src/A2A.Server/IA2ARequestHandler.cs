using System.Text.Json;
using A2A.Server.Streaming;

namespace A2A.Server;

/// <summary>
/// Every A2A operation, independent of the wire format. Each protocol binding (JSON-RPC, HTTP+JSON, gRPC, or your own)
/// parses its requests, builds a <see cref="ServerCallContext"/>, calls <see cref="ValidateCall"/>, then calls the operation.
/// Errors are thrown as <see cref="A2AException"/>; the binding maps them to its own error format.
/// </summary>
/// <remarks>
/// <see cref="A2ARequestHandler"/> is the implementation the DI container registers (<c>AddA2AServer</c>). The tasks,
/// messages and events these methods return may share instances with the stores: serialize them, never change them.
/// </remarks>
public interface IA2ARequestHandler
{
    /// <summary>The public Agent Card. Bindings use it, for example, to build authentication challenges.</summary>
    AgentCard AgentCard { get; }

    /// <summary>
    /// Checks the protocol version, required extensions, and authentication of a call, and fills
    /// <see cref="ServerCallContext.ActivatedExtensions"/>. Call it once per request, before the operation.
    /// </summary>
    /// <param name="call">The caller and the request headers.</param>
    void ValidateCall(ServerCallContext call);

    /// <summary>
    /// SendMessage (spec §3.1.1). Unless <see cref="SendMessageConfiguration.ReturnImmediately"/> is set, waits until the
    /// task is terminal or interrupted, or until no execution of the task is running any more.
    /// </summary>
    /// <param name="request">The message to send.</param>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The agent's reply message, or the task.</returns>
    Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, ServerCallContext call, CancellationToken cancellationToken = default);

    /// <summary>
    /// SendStreamingMessage (spec §3.1.2): sends a message and streams the task's events in sequence order, without repeats.
    /// </summary>
    /// <param name="request">The message to send.</param>
    /// <param name="call">The caller.</param>
    /// <param name="lastEventId">
    /// The <c>Last-Event-ID</c> the client sent, or null. A valid <see cref="StreamCursor"/> for the message's task (or for
    /// any task, when the message names none) makes the call a reconnect: it resumes that task's stream, authorized as
    /// SubscribeToTask, and the message is not sent again. Any other value is ignored and the message is sent normally.
    /// </param>
    /// <param name="onCaughtUp">
    /// Called once when a resume is accepted but has nothing to send yet (the client already has every event), so the
    /// binding can start its response at once instead of holding it until the next live event. Not called otherwise.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The events, each with its stored sequence (0 for an event that is not stored).</returns>
    IAsyncEnumerable<SequencedEvent> SendStreamingMessageAsync(SendMessageRequest request, ServerCallContext call,
        string? lastEventId = null, Action? onCaughtUp = null, CancellationToken cancellationToken = default);

    /// <summary>GetTask (spec §3.1.3). Throws TaskNotFound when the task does not exist or belongs to another caller.</summary>
    /// <param name="request">The task to get.</param>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The task.</returns>
    Task<AgentTask> GetTaskAsync(GetTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default);

    /// <summary>ListTasks (spec §3.1.4). Returns the caller's tasks, newest status first.</summary>
    /// <param name="request">Filters and paging.</param>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One page of tasks.</returns>
    Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, ServerCallContext call, CancellationToken cancellationToken = default);

    /// <summary>CancelTask (spec §3.1.5). Throws TaskNotCancelable when the task is already terminal.</summary>
    /// <param name="request">The task to cancel.</param>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The task after the cancel.</returns>
    Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, ServerCallContext call, CancellationToken cancellationToken = default);

    /// <summary>SubscribeToTask (spec §3.1.6), fresh or resumed. Events never repeat and no sequence is skipped.</summary>
    /// <param name="request">The task to stream.</param>
    /// <param name="call">The caller.</param>
    /// <param name="resumeAfter">
    /// Null for a fresh subscribe: a task snapshot, then live events. Otherwise the last sequence the client saw (from
    /// <c>Last-Event-ID</c>; a negative value means a cursor that could not be used): the retained events after it, or a
    /// snapshot when they are gone, then live events.
    /// </param>
    /// <param name="onCaughtUp">
    /// Called once when a resume is accepted but has nothing to send yet (the client already has every event), so the
    /// binding can start its response at once instead of holding it until the next live event. Not called otherwise.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The events, each with its stored sequence.</returns>
    IAsyncEnumerable<SequencedEvent> SubscribeToTaskAsync(SubscribeToTaskRequest request, ServerCallContext call,
        long? resumeAfter = null, Action? onCaughtUp = null, CancellationToken cancellationToken = default);

    /// <summary>CreateTaskPushNotificationConfig (spec §3.1.7). Validates the webhook URL and stores the registration.</summary>
    /// <param name="config">The registration.</param>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The stored registration, with secrets removed.</returns>
    Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, ServerCallContext call, CancellationToken cancellationToken = default);

    /// <summary>GetTaskPushNotificationConfig (spec §3.1.8).</summary>
    /// <param name="request">The registration to get.</param>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The registration, with secrets removed.</returns>
    Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default);

    /// <summary>ListTaskPushNotificationConfigs (spec §3.1.9).</summary>
    /// <param name="request">The task and paging.</param>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One page of registrations, with secrets removed.</returns>
    Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, ServerCallContext call, CancellationToken cancellationToken = default);

    /// <summary>DeleteTaskPushNotificationConfig (spec §3.1.10). Throws when the registration does not exist.</summary>
    /// <param name="request">The registration to delete.</param>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the registration is deleted.</returns>
    Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, ServerCallContext call, CancellationToken cancellationToken = default);

    /// <summary>GetExtendedAgentCard (spec §3.1.11). Needs an authenticated caller.</summary>
    /// <param name="request">The request.</param>
    /// <param name="call">The caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The extended Agent Card for the caller.</returns>
    Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, ServerCallContext call, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the extension operation registered as <paramref name="method"/> (see <see cref="Pipeline.A2AExtensionOperation"/>)
    /// through the operation pipeline, with <see cref="Pipeline.A2AOperationDescriptor.Extension"/> as its descriptor. When
    /// <paramref name="parameters"/> is an object with a string <c>tenant</c> property, that tenant is bound like the tenant
    /// of a built-in request. After authorization, the call must pass the tenant's card (a tenant no agent serves is UnsupportedOperation) and
    /// must have activated the operation's extension (the caller sent its URI in <see cref="ServerCallContext.RequestedExtensions"/>
    /// and the card lists it); otherwise the call fails with ExtensionSupportRequired and the handler does not run.
    /// Throws MethodNotFound when no extension operation has that name.
    /// </summary>
    /// <param name="method">The registered method name (case-sensitive).</param>
    /// <param name="parameters">The request params.</param>
    /// <param name="call">The caller. A binding that evaluated the operation's authorization policy sets <see cref="Pipeline.A2AExtensionOperation.PolicySatisfiedItem"/> in its <see cref="ServerCallContext.Items"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The operation's result.</returns>
    Task<JsonElement> InvokeExtensionAsync(string method, JsonElement parameters, ServerCallContext call, CancellationToken cancellationToken = default);
}
