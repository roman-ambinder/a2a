// Shared source, compiled into A2A.Extensions.AI and A2A.Extensions.AgentFramework (linked, not referenced: shipped
// packages do not use InternalsVisibleTo). The namespace differs per assembly so the two internal copies never share a
// full name. All types are internal.

#if A2A_AGENT_FRAMEWORK
namespace A2A.Extensions.AgentFramework.Shared;
#else
namespace A2A.Extensions.AI.Shared;
#endif

/// <summary>Fixed texts the executors use, so both packages say the same thing.</summary>
internal static class ExecutorTexts
{
    /// <summary>Status text of a task rejected because it has a URL part and URL parts are off.</summary>
    internal const string UrlRejected = "URL parts are not accepted by this agent.";

    /// <summary>Exception text (logged, never sent) when the model stops for its content filter.</summary>
    internal const string ContentFilter = "The model stopped because of its content filter.";
}
