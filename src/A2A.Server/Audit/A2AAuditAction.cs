namespace A2A.Server.Audit;

/// <summary>What an <see cref="A2AAuditEvent"/> records.</summary>
public enum A2AAuditAction
{
    /// <summary>A message that starts a new task was accepted.</summary>
    TaskCreated,
    /// <summary>A follow-up message was sent to an existing task.</summary>
    FollowUpSent,
    /// <summary>A follow-up carried the approval hash of the task's pending approval.</summary>
    ApprovalGranted,
    /// <summary>A follow-up answered a task that waits for approval, without the approval hash.</summary>
    ApprovalDeclined,
    /// <summary>A follow-up answered a form (an elicitation).</summary>
    ElicitationAnswered,
    /// <summary>CancelTask.</summary>
    TaskCanceled,
    /// <summary>A stored artifact object was opened for download.</summary>
    ArtifactDownloaded,
    /// <summary>A push notification config was created, directly or inline in SendMessage.</summary>
    PushConfigCreated,
    /// <summary>A push notification config was deleted.</summary>
    PushConfigDeleted,
    /// <summary>An operator deleted a task (<c>A2ATaskAdmin.DeleteTaskAsync</c>).</summary>
    AdminTaskDeleted,
    /// <summary>An operator sent a dead-letter push delivery again (<c>A2APushAdmin.RedriveAsync</c>).</summary>
    AdminDeadLetterRedriven,
    /// <summary>An operator listed the dead-letter push deliveries of a tenant (<c>A2APushAdmin.ListDeadLettersAsync</c>).</summary>
    AdminDeadLettersListed,
    /// <summary>A call was refused: not authenticated, or not allowed.</summary>
    AccessDenied,
}
