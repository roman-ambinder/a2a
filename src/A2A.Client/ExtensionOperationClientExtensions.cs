using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace A2A.Client;

/// <summary>
///     Client helpers for custom operations (<c>AddExtensionOperation</c> on the server): activate the extension of the
///     operation
///     for the call and invoke it.
/// </summary>
public static class ExtensionOperationClientExtensions
{
    /// <summary>
    ///     Calls the custom operation <paramref name="method" /> with raw JSON params. The extension
    ///     <paramref name="extensionUri" /> is
    ///     added to <paramref name="options" />, once (as <see cref="ElicitationClientExtensions.UseElicitation" /> does), so
    ///     the
    ///     server activates it. A plain A2A peer ignores the header and answers MethodNotFound.
    /// </summary>
    /// <param name="client">
    ///     The client of the agent. It must implement <see cref="IA2AExtensionOperationClient" />, as
    ///     <see cref="A2AClient" /> and the built-in transports do.
    /// </param>
    /// <param name="method">The operation name, as registered on the server.</param>
    /// <param name="extensionUri">The URI of the extension the operation belongs to.</param>
    /// <param name="parameters">
    ///     The params. See <see cref="IA2AExtensionOperationClient.InvokeExtensionOperationAsync" /> for
    ///     the tenant rules.
    /// </param>
    /// <param name="options">Options for the call, or null for new ones. These options are changed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result of the operation.</returns>
    /// <exception cref="ArgumentException">
    ///     <paramref name="method" /> is empty or reserved (a built-in method, or a name that
    ///     starts with <c>rpc.</c>).
    /// </exception>
    /// <exception cref="A2AException">
    ///     UnsupportedOperation when the client cannot call custom operations; otherwise the error
    ///     of the agent.
    /// </exception>
    public static Task<JsonElement> InvokeExtensionOperationAsync(this IA2AClient client, string method,
        string extensionUri, JsonElement parameters,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ExtensionOperationParams.RequireMethod(method);
        ArgumentException.ThrowIfNullOrEmpty(extensionUri);
        if (client is not IA2AExtensionOperationClient operations)
            throw new A2AException(A2AErrorCode.UnsupportedOperation,
                "This client does not support custom operations.");
        return operations.InvokeExtensionOperationAsync(method, parameters,
            UseExtension(options ?? new A2ARequestOptions(), extensionUri), cancellationToken);
    }

    /// <summary>
    ///     Calls the custom operation <paramref name="method" /> with a typed request and result. The types are read and
    ///     written with
    ///     the given type info, so the call is safe for trimming and native AOT. The extension is activated as in
    ///     <see
    ///         cref="InvokeExtensionOperationAsync(IA2AClient, string, string, JsonElement, A2ARequestOptions?, CancellationToken)" />
    ///     .
    /// </summary>
    /// <typeparam name="TRequest">The type of the request params.</typeparam>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="client">The client of the agent.</param>
    /// <param name="method">The operation name, as registered on the server.</param>
    /// <param name="extensionUri">The URI of the extension the operation belongs to.</param>
    /// <param name="request">The request. It is sent as the params.</param>
    /// <param name="requestType">The JSON type info used to write the request.</param>
    /// <param name="resultType">The JSON type info used to read the result.</param>
    /// <param name="options">Options for the call, or null for new ones. These options are changed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result of the operation.</returns>
    /// <exception cref="ArgumentException"><paramref name="method" /> is empty or reserved.</exception>
    /// <exception cref="A2AException">
    ///     The error of the agent, or InvalidAgentResponse when the result does not match
    ///     <typeparamref name="TResult" />.
    /// </exception>
    public static async Task<TResult> InvokeExtensionOperationAsync<TRequest, TResult>(this IA2AClient client,
        string method, string extensionUri,
        TRequest request, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResult> resultType,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(resultType);
        var parameters = JsonSerializer.SerializeToElement(request, requestType);
        var result = await client
            .InvokeExtensionOperationAsync(method, extensionUri, parameters, options, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return result.Deserialize(resultType) ?? throw new JsonException("The result is null.");
        }
        catch (JsonException ex)
        {
            throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                $"The result of '{method}' does not match the expected type: {ex.Message}", ex);
        }
    }

    private static A2ARequestOptions UseExtension(A2ARequestOptions options, string extensionUri)
    {
        if (options.Extensions is null or { IsReadOnly: true })
            options.Extensions = new List<string>(options.Extensions ?? []);
        if (!options.Extensions.Contains(extensionUri)) options.Extensions.Add(extensionUri);
        return options;
    }
}