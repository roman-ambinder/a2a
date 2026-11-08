namespace A2A.Server.Pipeline;

/// <summary>One call passing through the <see cref="IA2AOperationFilter"/> pipeline.</summary>
public sealed class A2AOperationContext
{
    private Dictionary<string, object?>? _items;
    private readonly object? _operationDelegate;
    private readonly Func<A2AOperationContext, object, CancellationToken, ValueTask<object?>>? _invoke;

    /// <summary>Creates a context with no operation behind it, for example to test a filter on its own.</summary>
    /// <param name="operation">The operation.</param>
    /// <param name="call">The caller.</param>
    public A2AOperationContext(A2AOperationDescriptor operation, ServerCallContext call)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(call);
        Operation = operation;
        Call = call;
    }

    /// <summary>A context whose innermost step runs <paramref name="operationDelegate"/> through <paramref name="invoke"/>.</summary>
    internal A2AOperationContext(A2AOperationDescriptor operation, ServerCallContext call, object operationDelegate,
        Func<A2AOperationContext, object, CancellationToken, ValueTask<object?>> invoke)
    {
        Operation = operation;
        Call = call;
        _operationDelegate = operationDelegate;
        _invoke = invoke;
    }

    /// <summary>The operation.</summary>
    public A2AOperationDescriptor Operation { get; }

    /// <summary>The caller, with its tenant already bound from the request.</summary>
    public ServerCallContext Call { get; }

    /// <summary>Data shared by the filters of this call. A new, empty dictionary per call (created when first read).</summary>
    public IDictionary<string, object?> Items => _items ??= new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>The operation delegate this context runs; read by <see cref="OperationInvoker{TRequest, TResponse}"/>.</summary>
    internal object? OperationDelegate => _operationDelegate;

    /// <summary>The innermost step: runs the operation with <paramref name="request"/>.</summary>
    internal ValueTask<object?> InvokeOperationAsync(object request, CancellationToken cancellationToken) =>
        _invoke is null
            ? throw new InvalidOperationException("This context has no operation behind it.")
            : _invoke(this, request, cancellationToken);
}
