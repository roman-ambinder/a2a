namespace A2A.Client.Push;

/// <summary>What <see cref="PushNotificationReceiver.HandleAsync" /> did with a delivery.</summary>
public enum PushHandleOutcome
{
    /// <summary>The handler ran and succeeded. Answer 200.</summary>
    Handled,

    /// <summary>The delivery id was handled before (a retry). The handler did not run. Answer 200 so the sender stops.</summary>
    Duplicate,

    /// <summary>The same delivery id is being handled by another call right now. Answer 503: the sender retries it later.</summary>
    InProgress,

    /// <summary>The handler threw. The id is forgotten, so the retry reaches the handler again. Answer 500.</summary>
    HandlerFailed,

    /// <summary>The token or signature is wrong, missing, or stale. The handler did not run. Answer 401.</summary>
    Unauthorized,

    /// <summary>
    ///     The credentials are right, but the body is not a valid <c>StreamResponse</c>. The handler did not run. Answer
    ///     400.
    /// </summary>
    BadRequest,

    /// <summary>
    ///     <see cref="PushNotificationReceiver.ExpectedTask" /> is set and said no: the event is for a task this client did
    ///     not create, or carries no task id (spec §4.3.3: clients MUST validate that the task id matches an expected
    ///     task). The handler did not run. Answer 404: the sender drops it and does not retry.
    /// </summary>
    UnexpectedTask
}

/// <summary>
///     A delivery that passed validation, as given to the handler of
///     <see cref="PushNotificationReceiver.HandleAsync" />.
/// </summary>
public sealed class PushDelivery
{
    /// <summary>Creates a delivery.</summary>
    public PushDelivery(StreamResponse evt, string? deliveryId, long? sequence)
    {
        Event = evt ?? throw new ArgumentNullException(nameof(evt));
        DeliveryId = deliveryId;
        Sequence = sequence;
    }

    /// <summary>The parsed notification body.</summary>
    public StreamResponse Event { get; }

    /// <summary>The delivery id (<c>X-A2A-Delivery-Id</c>, or <c>webhook-id</c>), or null when the sender gave none.</summary>
    public string? DeliveryId { get; }

    /// <summary>The event sequence (<c>X-A2A-Notification-Sequence</c>), or null when absent. Not every server sends it.</summary>
    public long? Sequence { get; }
}

/// <summary>
///     The result of <see cref="PushNotificationReceiver.HandleAsync" />: what happened and the HTTP status to answer
///     with.
/// </summary>
public sealed class PushHandleResult
{
    /// <summary>Creates a result.</summary>
    public PushHandleResult(PushHandleOutcome outcome, int statusCode, string? deliveryId = null,
        Exception? error = null)
    {
        Outcome = outcome;
        StatusCode = statusCode;
        DeliveryId = deliveryId;
        Error = error;
    }

    /// <summary>What happened.</summary>
    public PushHandleOutcome Outcome { get; }

    /// <summary>The HTTP status code to answer: 200, 400, 401, 500 or 503. A sender retries only 408, 429 and 5xx.</summary>
    public int StatusCode { get; }

    /// <summary>The delivery id, when there was one.</summary>
    public string? DeliveryId { get; }

    /// <summary>The exception the handler threw, for <see cref="PushHandleOutcome.HandlerFailed" />.</summary>
    public Exception? Error { get; }
}