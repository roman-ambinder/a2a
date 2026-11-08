using System.Security.Cryptography;
using System.Text;

namespace A2A.Server;

/// <summary>
/// An action the agent wants the user to confirm before it acts (approval binding, spec design §6.6). The hash binds the
/// approval to the exact action, so a client cannot confirm one thing and have another done (loopjacking).
/// </summary>
/// <param name="OperationId">
/// Identifies the operation, for example a tool call id. Must not contain a line feed (<c>'\n'</c>), which separates it from
/// the description in the hash.
/// </param>
/// <param name="Description">A canonical description of the exact action, as the user was shown it.</param>
/// <exception cref="ArgumentException"><paramref name="OperationId"/> is null or contains a line feed.</exception>
public sealed record PendingApproval(string OperationId, string Description)
{
    private readonly string _operationId = ValidOperationId(OperationId);

    /// <summary>Identifies the operation. Never contains a line feed.</summary>
    public string OperationId
    {
        get => _operationId;
        init => _operationId = ValidOperationId(value);
    }

    // Without this, ("a\nb", "c") and ("a", "b\nc") would have the same hash.
    private static string ValidOperationId(string operationId)
    {
        ArgumentNullException.ThrowIfNull(operationId, nameof(OperationId));
        if (operationId.Contains('\n')) throw new ArgumentException("The operation id must not contain a line feed.", nameof(operationId));
        return operationId;
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 bytes of <c>OperationId + "\n" + Description</c>.</summary>
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{OperationId}\n{Description}"))).ToLowerInvariant();
}

/// <summary>What a follow-up message says about the approval the task is waiting for (see <see cref="AgentRequestContext.Approval"/>).</summary>
public enum ApprovalState
{
    /// <summary>No approval is pending. The executor must not treat the message as an approval.</summary>
    None,

    /// <summary>The message carries the hash of the pending approval.</summary>
    Approved,

    /// <summary>An approval is pending and the message does not carry its hash.</summary>
    NotApproved,
}
