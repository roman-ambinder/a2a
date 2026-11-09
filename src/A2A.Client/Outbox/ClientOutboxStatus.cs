namespace A2A.Client.Outbox;

/// <summary>Where a <see cref="ClientOutboxEntry"/> is in its life. The numbers are stored by the database stores; they never change.</summary>
public enum ClientOutboxStatus
{
    /// <summary>Waiting to be sent to the peer.</summary>
    Queued = 0,

    /// <summary>The peer took the message and created a task. The dispatcher polls the task until it is final.</summary>
    Tracking = 1,

    /// <summary>The peer gave a final answer (a terminal or interrupted task, or a message). The answer is stored. Waiting to be collected.</summary>
    Finished = 2,

    /// <summary>The SDK could not get an answer: the peer refused the send, time ran out, or the outcome is in doubt and <see cref="ClientOutboxOptions.RetryInDoubt"/> is off. Waiting to be collected.</summary>
    Failed = 3,
}
