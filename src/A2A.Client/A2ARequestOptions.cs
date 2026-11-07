using System.Net.Http;

namespace A2A.Client;

/// <summary>
/// Options for one client call. Pass it as the <c>options</c> argument of any <see cref="IA2AClient"/> method.
/// </summary>
/// <remarks>
/// <para>
/// Header order on every HTTP request: the SDK sets <c>A2A-Version</c>, <c>A2A-Extensions</c>, <c>Accept</c> and
/// <c>Idempotency-Key</c> first. Then <see cref="Headers"/> are applied (a key replaces the SDK value of the same name).
/// Then <see cref="ConfigureRequest"/> runs last and sees the final request. Both can therefore overwrite or remove
/// <c>A2A-Version</c>. The SDK does not stop you; do not do it.
/// </para>
/// <para>The same options object is applied again on every automatic retry.</para>
/// </remarks>
public sealed class A2ARequestOptions
{
    /// <summary>Extra HTTP headers for this call.</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>Idempotency-Key</c> for <c>SendMessage</c> and <c>SendStreamingMessage</c>. Default: the message id.
    /// Ignored by other methods.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Time limit for one attempt of a unary call. Default 100 seconds. Streaming calls have no time limit and ignore this.
    /// Use <see cref="Timeout.InfiniteTimeSpan"/> to disable it.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Extension URIs for this call. Added to <see cref="A2AClientOptions.Extensions"/>.</summary>
    public IList<string>? Extensions { get; set; }

    /// <summary>
    /// Last hook before a request is sent, for example to sign it or add a per-call credential. Runs after
    /// <see cref="Headers"/>, once per attempt.
    /// </summary>
    public Func<HttpRequestMessage, CancellationToken, ValueTask>? ConfigureRequest { get; set; }
}
