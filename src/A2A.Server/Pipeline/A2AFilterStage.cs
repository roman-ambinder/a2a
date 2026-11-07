namespace A2A.Server.Pipeline;

/// <summary>Where an <see cref="IA2AOperationFilter"/> runs relative to the built-in authorization filter.</summary>
public enum A2AFilterStage
{
    /// <summary>
    /// Before authorization. The filter sees every call, including unauthenticated and unauthorized ones that authorization
    /// will refuse; do not trust the caller's identity here. Use it for work that must see refused calls (for example counting them).
    /// </summary>
    BeforeAuthorization,

    /// <summary>After authorization (the default). The filter only sees calls that are authenticated (or allowed anonymous) and authorized.</summary>
    AfterAuthorization,
}
