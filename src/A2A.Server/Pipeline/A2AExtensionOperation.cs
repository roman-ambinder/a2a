using System.Text.Json;
using A2A.Server.Authorization;

namespace A2A.Server.Pipeline;

/// <summary>
/// An extension operation a host registered with <see cref="A2AServerBuilder.AddExtensionOperation"/>. The JSON-RPC binding
/// serves it as the method <see cref="Method"/>; the HTTP+JSON binding as <c>POST {pattern}/ext/{Method}</c>. The gRPC binding
/// does not serve extension operations (its service definition is fixed).
/// </summary>
/// <remarks>
/// Every call runs through the operation pipeline like a built-in operation: tenant binding, telemetry, the host filters and
/// authorization, with <see cref="A2AOperationDescriptor.Extension"/> as its descriptor. The request the filters see is the
/// <see cref="JsonElement"/> params (boxed), and the result is the <see cref="JsonElement"/> the handler returns. The default
/// <see cref="A2AAuthorizationHandler"/> allows the call only when <see cref="Allow"/> is true or the
/// <see cref="AuthorizationPolicy"/> was satisfied; otherwise it denies it (fail closed).
/// </remarks>
/// <param name="Method">The JSON-RPC method name, and the last segment of the HTTP+JSON route. Letters, digits, <c>.</c>, <c>_</c> and <c>-</c>.</param>
/// <param name="ExtensionUri">The absolute URI of the extension. The public Agent Card lists it in <see cref="AgentCapabilities.Extensions"/>.</param>
/// <param name="Handler">Runs the operation with the call's pipeline context and the request params; returns the result.</param>
/// <param name="AuthorizationPolicy">
/// The name of an ASP.NET Core authorization policy. The HTTP bindings evaluate it for <c>HttpContext.User</c> before the call
/// and refuse the call when it fails (the same error as an authorization handler denial); when it succeeds they set
/// <see cref="PolicySatisfiedItem"/> to true in <see cref="ServerCallContext.Items"/>, which the pipeline copies to
/// <see cref="A2AOperationContext.Items"/>. Hosts without ASP.NET Core (or with their own binding) use <see cref="Allow"/> or a
/// custom <see cref="A2AAuthorizationHandler"/> instead: nothing else evaluates the policy.
/// </param>
/// <param name="Allow">
/// True to let the default <see cref="A2AAuthorizationHandler"/> allow every authenticated caller. A custom handler decides
/// on its own, by <see cref="A2AOperationDescriptor.Kind"/> and <see cref="A2AOperationDescriptor.Name"/>.
/// </param>
public sealed record A2AExtensionOperation(
    string Method,
    string ExtensionUri,
    Func<A2AOperationContext, JsonElement, CancellationToken, ValueTask<JsonElement>> Handler,
    string? AuthorizationPolicy = null,
    bool Allow = false)
{
    /// <summary>
    /// The <see cref="ServerCallContext.Items"/> and <see cref="A2AOperationContext.Items"/> key whose value is <c>true</c> when
    /// the binding evaluated <see cref="AuthorizationPolicy"/> and it succeeded.
    /// </summary>
    public const string PolicySatisfiedItem = "a2a.extension.policySatisfied";

    /// <summary>The 11 built-in JSON-RPC method names: an extension operation may not use one (compared ignoring case).</summary>
    private static readonly HashSet<string> BuiltInMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        A2AProtocol.Methods.SendMessage,
        A2AProtocol.Methods.SendStreamingMessage,
        A2AProtocol.Methods.GetTask,
        A2AProtocol.Methods.ListTasks,
        A2AProtocol.Methods.CancelTask,
        A2AProtocol.Methods.SubscribeToTask,
        A2AProtocol.Methods.CreateTaskPushNotificationConfig,
        A2AProtocol.Methods.GetTaskPushNotificationConfig,
        A2AProtocol.Methods.ListTaskPushNotificationConfigs,
        A2AProtocol.Methods.DeleteTaskPushNotificationConfig,
        A2AProtocol.Methods.GetExtendedAgentCard,
    };

    /// <summary>
    /// Checks a registration: the method name is made of letters, digits, <c>.</c>, <c>_</c> and <c>-</c> (and is not only dots,
    /// which a URL path cannot carry), is not a built-in method, and the extension URI is absolute.
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
                throw new ArgumentException($"'{method}' is not a valid extension method name: use only letters, digits, '.', '_' and '-'.", nameof(method));
            onlyDots &= c == '.';
        }
        if (onlyDots) throw new ArgumentException($"'{method}' is not a valid extension method name.", nameof(method));
        if (BuiltInMethods.Contains(method))
            throw new ArgumentException($"'{method}' is a built-in A2A method; an extension operation needs a name of its own.", nameof(method));
        // On Unix, "/path" parses as an absolute file URI: a rooted path is not an extension URI.
        if (extensionUri[0] is '/' or '\\' || !Uri.TryCreate(extensionUri, UriKind.Absolute, out _))
            throw new ArgumentException($"'{extensionUri}' is not an absolute URI.", nameof(extensionUri));
    }
}
