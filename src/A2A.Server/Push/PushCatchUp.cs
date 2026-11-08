using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using A2A.Diagnostics;
using A2A.Server.Storage;

namespace A2A.Server.Push;

/// <summary>
/// The catch-up delivery of a webhook that was registered after the event it should see: one delivery of a task snapshot,
/// for one config, at the task's latest event sequence. Queued by the request handler when a config is created, and by the
/// engine when it finds a config its pre-commit listing missed. Both can queue it for one config; the outbox keeps one
/// (<see cref="PushOutbox.EnqueueAsync"/> is idempotent on task, config and sequence), and the delivery id is derived from that
/// key, so even a resend after the first one was delivered and removed carries the same id.
/// </summary>
internal static class PushCatchUp
{
    /// <summary>A state after which no event follows until someone acts: terminal, or waiting for input or authorization.</summary>
    public static bool IsFinalForNow(TaskState state) => state.IsTerminal() || state.IsInterrupted();

    /// <summary>The delivery of a catch-up: the id is a hash of the key, the trace context is the current activity's.</summary>
    public static PushDelivery NewDelivery(TaskKey key, string configId, long sequence)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\0', key.Tenant, key.Owner, key.TaskId, configId, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        string? traceParent = null;
        A2ATelemetry.InjectTraceContext(Activity.Current, (name, value) =>
        {
            if (name == A2ATelemetry.TraceParentHeader) traceParent = value;
        });
        return new PushDelivery(key, configId, sequence, Convert.ToHexString(hash, 0, 16).ToLowerInvariant()) { TraceParent = traceParent };
    }
}
