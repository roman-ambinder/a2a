using System.Text.Json;

namespace A2A.Client;

/// <summary>
/// Calls a custom operation of an agent: a JSON-RPC method, <c>POST /ext/{method}</c> or a gRPC <c>InvokeExtensionOperation</c>,
/// registered on the server with <c>AddExtensionOperation</c>. Implemented by <see cref="A2AClient"/> and by the built-in
/// transports. It is a separate interface so that <see cref="IA2AClient"/> stays as it is for other transports.
/// </summary>
/// <remarks>
/// Most callers use <see cref="ExtensionOperationClientExtensions"/>, which also activates the extension for the call. The
/// calls are unary and are retried only when the request was never sent (<see cref="Resilience.RetryMode.UnsentOnly"/>),
/// because the SDK cannot know that the operation is idempotent.
/// </remarks>
public interface IA2AExtensionOperationClient
{
    /// <summary>Calls the custom operation <paramref name="method"/> and returns its result.</summary>
    /// <param name="method">The operation name, as registered on the server. It must not be a reserved name.</param>
    /// <param name="parameters">
    /// The params. An undefined value is sent as the empty object. A string <c>tenant</c> member selects the tenant; else the
    /// tenant of the client options is used. On JSON-RPC and gRPC the tenant of the options is added as a <c>tenant</c> member, so
    /// the params must then be a JSON object.
    /// </param>
    /// <param name="options">Options for the call. The extension of the operation must be in <see cref="A2ARequestOptions.Extensions"/> or in the client options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result of the operation. An empty gRPC result is the empty object.</returns>
    /// <exception cref="ArgumentException"><paramref name="method"/> is empty or reserved.</exception>
    /// <exception cref="A2AException">The agent refused the call, or the params are not an object while a tenant must be added (InvalidParams).</exception>
    Task<JsonElement> InvokeExtensionOperationAsync(string method, JsonElement parameters, A2ARequestOptions? options = null, CancellationToken cancellationToken = default);
}
