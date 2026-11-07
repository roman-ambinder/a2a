namespace A2A.Server.Pipeline;

/// <summary>
/// A host filter and its stage, as <see cref="A2AServerBuilder.AddOperationFilter{T}"/> registers it in the DI container.
/// The request handler reads every registration once, in registration order, when it is created.
/// </summary>
public sealed class A2AOperationFilterRegistration
{
    /// <summary>Creates a registration.</summary>
    /// <param name="filter">The filter.</param>
    /// <param name="stage">Where it runs relative to authorization.</param>
    public A2AOperationFilterRegistration(IA2AOperationFilter filter, A2AFilterStage stage = A2AFilterStage.AfterAuthorization)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (stage is not (A2AFilterStage.BeforeAuthorization or A2AFilterStage.AfterAuthorization))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown filter stage.");
        Filter = filter;
        Stage = stage;
    }

    /// <summary>The filter.</summary>
    public IA2AOperationFilter Filter { get; }

    /// <summary>Where the filter runs relative to authorization.</summary>
    public A2AFilterStage Stage { get; }
}
