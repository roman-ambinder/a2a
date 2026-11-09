using System.Collections.Frozen;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;

namespace A2A.Server.AspNetCore.Operations;

/// <summary>
/// The operations the HTTP bindings serve: the 11 built-in JSON-RPC methods and HTTP+JSON routes, each with the shared
/// <see cref="A2AOperationDescriptor"/> of its operation, plus the registered extension operations
/// (<see cref="A2AServerBuilder.AddExtensionOperation"/>): a JSON-RPC method and a <c>POST /ext/{method}</c> route each.
/// <see cref="JsonRpcEndpoint"/> and <see cref="RestEndpoint"/> dispatch through it. The gRPC extension service
/// (<c>a2a.extensions.v1.A2AExtensionService</c>) looks the operations up with <see cref="TryGetExtension"/>; the gRPC
/// <c>A2AService</c> does not use the table, because its service definition is fixed.
/// </summary>
public sealed class A2AOperationTable
{
    private readonly FrozenDictionary<string, A2AOperationDescriptor> _methods;
    private readonly FrozenDictionary<string, A2AExtensionOperation> _extensions;

    /// <summary>Builds the table. In an ASP.NET Core app the DI container does this, with the registered extension operations.</summary>
    /// <param name="extensions">The registered extension operations.</param>
    /// <exception cref="InvalidOperationException">Two extension operations have the same name (ignoring case), or one has a reserved name (<see cref="A2AProtocol.Methods.IsReserved"/>).</exception>
    public A2AOperationTable(IEnumerable<A2AExtensionOperation> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var methods = new Dictionary<string, A2AOperationDescriptor>(StringComparer.Ordinal)
        {
            [A2AProtocol.Methods.SendMessage] = A2AOperationDescriptor.For(A2AOperation.SendMessage),
            [A2AProtocol.Methods.SendStreamingMessage] = A2AOperationDescriptor.For(A2AOperation.SendStreamingMessage),
            [A2AProtocol.Methods.GetTask] = A2AOperationDescriptor.For(A2AOperation.GetTask),
            [A2AProtocol.Methods.ListTasks] = A2AOperationDescriptor.For(A2AOperation.ListTasks),
            [A2AProtocol.Methods.CancelTask] = A2AOperationDescriptor.For(A2AOperation.CancelTask),
            [A2AProtocol.Methods.SubscribeToTask] = A2AOperationDescriptor.For(A2AOperation.SubscribeToTask),
            [A2AProtocol.Methods.CreateTaskPushNotificationConfig] = A2AOperationDescriptor.For(A2AOperation.CreateTaskPushNotificationConfig),
            [A2AProtocol.Methods.GetTaskPushNotificationConfig] = A2AOperationDescriptor.For(A2AOperation.GetTaskPushNotificationConfig),
            [A2AProtocol.Methods.ListTaskPushNotificationConfigs] = A2AOperationDescriptor.For(A2AOperation.ListTaskPushNotificationConfigs),
            [A2AProtocol.Methods.DeleteTaskPushNotificationConfig] = A2AOperationDescriptor.For(A2AOperation.DeleteTaskPushNotificationConfig),
            [A2AProtocol.Methods.GetExtendedAgentCard] = A2AOperationDescriptor.For(A2AOperation.GetExtendedAgentCard),
        };
        // Built-in routes, in the order they have always been mapped. The spec text says POST for subscribe, the normative
        // proto says GET: both are accepted.
        var routes = new List<(string Method, string Route, A2AOperationDescriptor Op)>
        {
            ("POST", "/message:send", A2AOperationDescriptor.For(A2AOperation.SendMessage)),
            ("POST", "/message:stream", A2AOperationDescriptor.For(A2AOperation.SendStreamingMessage)),
            ("GET", "/tasks", A2AOperationDescriptor.For(A2AOperation.ListTasks)),
            ("GET", "/tasks/{id}", A2AOperationDescriptor.For(A2AOperation.GetTask)),
            ("POST", "/tasks/{id}:cancel", A2AOperationDescriptor.For(A2AOperation.CancelTask)),
            ("GET|POST", "/tasks/{id}:subscribe", A2AOperationDescriptor.For(A2AOperation.SubscribeToTask)),
            ("POST", "/tasks/{id}/pushNotificationConfigs", A2AOperationDescriptor.For(A2AOperation.CreateTaskPushNotificationConfig)),
            ("GET", "/tasks/{id}/pushNotificationConfigs", A2AOperationDescriptor.For(A2AOperation.ListTaskPushNotificationConfigs)),
            ("GET", "/tasks/{id}/pushNotificationConfigs/{configId}", A2AOperationDescriptor.For(A2AOperation.GetTaskPushNotificationConfig)),
            ("DELETE", "/tasks/{id}/pushNotificationConfigs/{configId}", A2AOperationDescriptor.For(A2AOperation.DeleteTaskPushNotificationConfig)),
            ("GET", "/extendedAgentCard", A2AOperationDescriptor.For(A2AOperation.GetExtendedAgentCard)),
        };

        var list = new List<A2AExtensionOperation>();
        var byName = new Dictionary<string, A2AExtensionOperation>(StringComparer.OrdinalIgnoreCase); // HTTP routes ignore case
        foreach (var extension in extensions)
        {
            if (A2AProtocol.Methods.IsReserved(extension.Method))
                throw new InvalidOperationException($"The extension operation '{extension.Method}' has a reserved name (a built-in A2A method, or a name that starts with 'rpc.').");
            if (!byName.TryAdd(extension.Method, extension))
                throw new InvalidOperationException($"More than one extension operation is registered as '{extension.Method}'.");
            var descriptor = A2AOperationDescriptor.Extension(extension.Method);
            methods.Add(extension.Method, descriptor);
            routes.Add(("POST", "/ext/" + extension.Method, descriptor));
            list.Add(extension);
        }

        _methods = methods.ToFrozenDictionary(StringComparer.Ordinal);
        _extensions = list.ToFrozenDictionary(e => e.Method, StringComparer.Ordinal);
        RestRoutes = routes.AsReadOnly();
        Extensions = list.AsReadOnly();
    }

    /// <summary>
    /// JSON-RPC method name (case-sensitive) to operation: the 11 built-in methods, then one entry per extension operation
    /// (<see cref="A2AOperation.Extension"/>, named by its method).
    /// </summary>
    public IReadOnlyDictionary<string, A2AOperationDescriptor> JsonRpcMethods => _methods;

    /// <summary>
    /// The HTTP+JSON routes, relative to the mapped prefix (each also gets a <c>/{tenant}</c> variant unless the prefix has a
    /// tenant segment): HTTP method (<c>GET|POST</c> when both are accepted), route template, operation. The 11 built-in routes
    /// come first, then <c>POST /ext/{method}</c> per extension operation.
    /// </summary>
    public IReadOnlyList<(string Method, string Route, A2AOperationDescriptor Op)> RestRoutes { get; }

    /// <summary>The registered extension operations, in registration order.</summary>
    public IReadOnlyList<A2AExtensionOperation> Extensions { get; }

    /// <summary>The extension operation registered as <paramref name="method"/> (case-sensitive).</summary>
    internal A2AExtensionOperation GetExtension(string method) => _extensions[method];

    /// <summary>Finds the extension operation registered as <paramref name="method"/> (case-sensitive, ordinal).</summary>
    /// <param name="method">The operation name: the JSON-RPC method, and the <c>operation_id</c> of the gRPC extension service.</param>
    /// <param name="operation">The operation, when found.</param>
    /// <returns>True when an operation with that name is registered.</returns>
    public bool TryGetExtension(string method, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out A2AExtensionOperation? operation) =>
        _extensions.TryGetValue(method, out operation);
}
