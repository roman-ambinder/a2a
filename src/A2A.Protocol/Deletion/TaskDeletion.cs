namespace A2A.Deletion;

/// <summary>
///     Task deletion: an SDK extension operation that lets a caller delete a task it owns, with all its data (spec
///     §13.4: agents SHOULD let users request deletion of their data; A2A 1.0 defines no delete operation). A server
///     serves it only after <c>AddTaskDeletion()</c>, and then lists <see cref="ExtensionUri" /> on its Agent Card. A
///     call names the task in the <see cref="IdParam" /> member of the params (and the tenant as usual); the result is the
///     empty object. Only the owner can delete, and only a task in a terminal state: another caller's task, or a missing
///     one, is TaskNotFound, and a task that is not terminal is UnsupportedOperation (cancel it first).
/// </summary>
public static class TaskDeletion
{
    /// <summary>URI of the optional task-deletion extension.</summary>
    public const string ExtensionUri = "https://github.com/roman-ambinder/a2a/extensions/task-deletion/v1";

    /// <summary>
    ///     The operation name: the JSON-RPC method, the last segment of the HTTP+JSON route
    ///     (<c>POST {pattern}/ext/a2a.tasks.delete</c>) and the gRPC <c>operation_id</c>.
    /// </summary>
    public const string Method = "a2a.tasks.delete";

    /// <summary>The params member that holds the id of the task to delete.</summary>
    public const string IdParam = "id";
}
