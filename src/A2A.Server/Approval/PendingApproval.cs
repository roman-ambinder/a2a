using System.Security.Cryptography;
using System.Text;

namespace A2A.Server;

/// <summary>
/// An action the agent wants the user to confirm before it acts (approval binding, spec design §6.6). The hash binds the
/// approval to the exact action, so a client cannot confirm one thing and have another done (loopjacking).
/// </summary>
/// <param name="OperationId">Identifies the operation, for example a tool call id.</param>
/// <param name="Description">A canonical description of the exact action, as the user was shown it.</param>
public sealed record PendingApproval(string OperationId, string Description)
{
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
