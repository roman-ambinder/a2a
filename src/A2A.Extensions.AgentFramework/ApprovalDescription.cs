using System.Text.Json;
using A2A.Signing;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AgentFramework;

/// <summary>The text a caller approves, and the hash binds: one canonical description of one tool call.</summary>
internal static class ApprovalDescription
{
    /// <summary>
    ///     For a <see cref="FunctionCallContent" />: <c>name(arguments)</c>, the arguments as RFC 8785 canonical JSON, so the
    ///     same call always gives the same text, whatever the key order and whether the values are CLR objects or
    ///     <see cref="JsonElement" />s (as they are after a session round trip). For any other tool call: its type name and
    ///     call id.
    /// </summary>
    /// <exception cref="NotSupportedException">An argument value has no JSON contract in the M.E.AI options.</exception>
    internal static string For(ToolCallContent call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call is not FunctionCallContent function) return $"{call.GetType().Name} {call.CallId}";
        // Source-generated M.E.AI contracts (AOT safe): the same ones that serialize FunctionCallContent.Arguments.
        var typeInfo = AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>));
        var arguments = function.Arguments ?? new Dictionary<string, object?>();
        var json = JsonSerializer.Serialize(arguments, typeInfo);
        return $"{function.Name}({JsonCanonicalizer.CanonicalizeToString(json)})";
    }
}
