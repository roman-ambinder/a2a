using System.Text.Json;
using A2A.Server.Authorization;

namespace A2A.Server.Pipeline;

/// <summary>
///     An extension operation a host registered with <see cref="A2AServerBuilder.AddExtensionOperation" />. The JSON-RPC
///     binding
///     serves it as the method <see cref="Method" />; the HTTP+JSON binding as <c>POST {pattern}/ext/{Method}</c>. The
///     gRPC binding
///     serves it through <c>a2a.extensions.v1.A2AExtensionService</c> (<c>InvokeExtensionOperation</c>, with
///     <see cref="Method" /> as the
///     <c>operation_id</c> and JSON payloads), the service definition of the official A2A .NET SDK; its streaming RPC is
///     not served.
/// </summary>
/// <remarks>
///     Every call runs through the operation pipeline like a built-in operation: tenant binding, telemetry, the host
///     filters and
///     authorization, with <see cref="A2AOperationDescriptor.Extension" /> as its descriptor. The request the filters see
///     is the
///     <see cref="JsonElement" /> params (boxed), and the result is the <see cref="JsonElement" /> the handler returns.
///     The default
///     <see cref="A2AAuthorizationHandler" /> allows the call only when <see cref="Allow" /> is true or the
///     <see cref="AuthorizationPolicy" /> was satisfied; otherwise it denies it (fail closed). After authorization, and
///     before
///     <see cref="Handler" />, the core resolves the card of the call's tenant (a tenant no agent serves is
///     UnsupportedOperation; with
///     per-tenant cards, this also enforces the tenant's required extensions) and requires that the call activated
///     <see cref="ExtensionUri" />: the caller sent it in <c>A2A-Extensions</c> and the card lists it. Otherwise the call
///     fails with
///     ExtensionSupportRequired and the handler does not run. The default card lists the URI by itself; a host with its
///     own
///     <see cref="IAgentCardProvider" /> must declare it. A policy denial in the HTTP
///     bindings happens before the pipeline: the call gets no span and no counter, and
///     <see cref="A2AFilterStage.BeforeAuthorization" />
///     filters do not see it.
/// </remarks>
/// <param name="Method">
///     The JSON-RPC method name, and the last segment of the HTTP+JSON route. Letters, digits, <c>.</c>,
///     <c>_</c> and <c>-</c>. Not a reserved name (<see cref="A2AProtocol.Methods.IsReserved" />).
/// </param>
/// <param name="ExtensionUri">
///     The absolute URI of the extension. The public Agent Card lists it in
///     <see cref="AgentCapabilities.Extensions" />. A call must activate it.
/// </param>
/// <param name="Handler">Runs the operation with the call's pipeline context and the request params; returns the result.</param>
/// <param name="AuthorizationPolicy">
///     The name of an ASP.NET Core authorization policy. The HTTP bindings evaluate it for <c>HttpContext.User</c> before
///     the call
///     and refuse the call when it fails (the same error as an authorization handler denial); when it succeeds they set
///     <see cref="PolicySatisfiedItem" /> to true in <see cref="ServerCallContext.Items" />, which the pipeline copies to
///     <see cref="A2AOperationContext.Items" />. Hosts without ASP.NET Core (or with their own binding) use
///     <see cref="Allow" /> or a
///     custom <see cref="A2AAuthorizationHandler" /> instead: nothing else evaluates the policy.
/// </param>
/// <param name="Allow">
///     True to let the default <see cref="A2AAuthorizationHandler" /> allow every caller the endpoint admits, including
///     anonymous callers when <see cref="A2AServerOptions.AllowAnonymous" /> is set. A custom handler decides
///     on its own, by <see cref="A2AOperationDescriptor.Kind" /> and <see cref="A2AOperationDescriptor.Name" />.
/// </param>
public sealed record A2AExtensionOperation(
    string Method,
    string ExtensionUri,
    Func<A2AOperationContext, JsonElement, CancellationToken, ValueTask<JsonElement>> Handler,
    string? AuthorizationPolicy = null,
    bool Allow = false)
{
    /// <summary>
    ///     The <see cref="ServerCallContext.Items" /> and <see cref="A2AOperationContext.Items" /> key whose value is
    ///     <c>true</c> when
    ///     the binding evaluated <see cref="AuthorizationPolicy" /> and it succeeded.
    /// </summary>
    public const string PolicySatisfiedItem = "a2a.extension.policySatisfied";

    /// <summary>
    ///     The string member of the params that names a task, for an SDK operation that is task-scoped (task deletion).
    ///     A denial of such a call names that task and looks like a missing task (TaskNotFound), as it does for the built-in
    ///     task operations. Null for every operation a host registers.
    /// </summary>
    internal string? TaskIdMember { get; init; }

    /// <summary>
    ///     Checks a registration: the method name is made of letters, digits, <c>.</c>, <c>_</c> and <c>-</c> (and is not only
    ///     dots,
    ///     which a URL path cannot carry), is not reserved (see <see cref="A2AProtocol.Methods.IsReserved" />), and the
    ///     extension URI is absolute.
    /// </summary>
    internal static void Validate(string method, string extensionUri, object handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentException.ThrowIfNullOrEmpty(extensionUri);
        ArgumentNullException.ThrowIfNull(handler);
        var onlyDots = true;
        foreach (var c in method)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                throw new ArgumentException(
                    $"'{method}' is not a valid extension method name: use only letters, digits, '.', '_' and '-'.",
                    nameof(method));
            onlyDots &= c == '.';
        }

        if (onlyDots) throw new ArgumentException($"'{method}' is not a valid extension method name.", nameof(method));
        if (A2AProtocol.Methods.IsReserved(method))
            throw new ArgumentException(
                $"'{method}' is reserved (a built-in A2A method, or a name that starts with 'rpc.'); an extension operation needs a name of its own.",
                nameof(method));
        // On Unix, "/path" parses as an absolute file URI: a rooted path is not an extension URI.
        if (extensionUri[0] is '/' or '\\' || !Uri.TryCreate(extensionUri, UriKind.Absolute, out _))
            throw new ArgumentException($"'{extensionUri}' is not an absolute URI.", nameof(extensionUri));
    }
}