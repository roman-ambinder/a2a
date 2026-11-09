using System.Text.Json;
using A2A.Json;

namespace A2A.Client;

/// <summary>The rules for custom operation calls that every binding shares: the method name, the params and the tenant.</summary>
internal static class ExtensionOperationParams
{
    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="method"/> is empty or a reserved name.</summary>
    public static void RequireMethod(string method)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        if (A2AProtocol.Methods.IsReserved(method))
            throw new ArgumentException($"'{method}' is reserved (a built-in A2A method, or a name that starts with 'rpc.').", nameof(method));
    }

    /// <summary>The params to send: an undefined value is the empty object.</summary>
    public static JsonElement Normalize(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Undefined ? A2AJson.EmptyObject : parameters;

    /// <summary>The string <c>tenant</c> member of object params, or null.</summary>
    public static string? TenantOf(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("tenant", out var tenant) && tenant.ValueKind == JsonValueKind.String
            ? tenant.GetString()
            : null;

    /// <summary>
    /// The params for a binding that carries the tenant in the params (JSON-RPC and gRPC). Params that have a <c>tenant</c> member
    /// stay as they are (that tenant wins). Otherwise the tenant <paramref name="optionsTenant"/> is added to an object. Params
    /// that are not an object cannot take a tenant: that is InvalidParams, because a call to the wrong tenant is worse.
    /// </summary>
    public static JsonElement WithTenant(JsonElement parameters, string? optionsTenant)
    {
        parameters = Normalize(parameters);
        if (string.IsNullOrEmpty(optionsTenant)) return parameters;
        if (parameters.ValueKind != JsonValueKind.Object)
            throw A2AException.InvalidParams("The params must be a JSON object when a tenant is set, so that the tenant can be added.");
        if (parameters.TryGetProperty("tenant", out _)) return parameters;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("tenant", optionsTenant);
            foreach (var member in parameters.EnumerateObject()) member.WriteTo(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
