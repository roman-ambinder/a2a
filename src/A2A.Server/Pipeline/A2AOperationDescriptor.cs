using A2A.Server.Authorization;

namespace A2A.Server.Pipeline;

/// <summary>
/// Names an operation for the <see cref="IA2AOperationFilter"/> pipeline and the <see cref="A2AAuthorizationHandler"/>.
/// The 11 built-in operations have one shared descriptor each (<see cref="For"/>), named like their <see cref="A2AOperation"/>
/// value; extension operations use <see cref="A2AOperation.Extension"/> and the name they were registered with (<see cref="Extension"/>).
/// </summary>
/// <param name="Kind">The operation. <see cref="A2AOperation.Extension"/> for an extension operation.</param>
/// <param name="Name">The operation name: the <see cref="A2AOperation"/> name for a built-in operation (for example <c>GetTask</c>), the registered name for an extension operation.</param>
public sealed record A2AOperationDescriptor(A2AOperation Kind, string Name)
{
    private static readonly A2AOperationDescriptor[] BuiltIn = CreateBuiltIn();

    /// <summary>The shared descriptor of a built-in operation.</summary>
    /// <param name="kind">A built-in operation; not <see cref="A2AOperation.Extension"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is <see cref="A2AOperation.Extension"/> or not a defined value.</exception>
    public static A2AOperationDescriptor For(A2AOperation kind) =>
        (uint)kind < (uint)BuiltIn.Length
            ? BuiltIn[(int)kind]
            : throw new ArgumentException($"{kind} is not a built-in operation. Use Extension(name) for an extension operation.", nameof(kind));

    /// <summary>A descriptor for the extension operation registered as <paramref name="name"/>.</summary>
    /// <param name="name">The registered name, for example <c>x.echo</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    public static A2AOperationDescriptor Extension(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new A2AOperationDescriptor(A2AOperation.Extension, name);
    }

    private static A2AOperationDescriptor[] CreateBuiltIn()
    {
        // The built-in operations are the enum values before Extension, numbered from 0.
        var all = new A2AOperationDescriptor[(int)A2AOperation.Extension];
        for (var i = 0; i < all.Length; i++)
        {
            var kind = (A2AOperation)i;
            all[i] = new A2AOperationDescriptor(kind, kind.ToString());
        }
        return all;
    }
}
