using System.Text.Json;
using System.Text.Json.Serialization;

namespace A2A.JsonRpc;

/// <summary>JSON-RPC 2.0 request envelope.</summary>
public sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    /// <summary>String, number, or null. Absent for notifications (not used by A2A).</summary>
    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

/// <summary>JSON-RPC 2.0 response envelope. Exactly one of <see cref="Result"/> or <see cref="Error"/> is set.</summary>
public sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    /// <summary>Echo of the request id. Serialized as null when unknown (e.g. parse errors).</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    public static JsonRpcResponse Success(JsonElement? id, JsonElement result) => new() { Id = id, Result = result };

    public static JsonRpcResponse Failure(JsonElement? id, A2AException ex) => new()
    {
        Id = id,
        Error = new JsonRpcError
        {
            // Auth errors have no JSON-RPC number in the spec; send Internal and let the ErrorInfo reason carry the type.
            Code = ex.Code is A2AErrorCode.Unauthenticated or A2AErrorCode.PermissionDenied ? (int)A2AErrorCode.Internal : (int)ex.Code,
            Message = ex.Message,
            Data = A2AErrors.BuildDetails(ex),
        },
    };
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Array of ProtoJSON <c>Any</c> detail objects (spec §9.5). Older peers may send any JSON value.</summary>
    [JsonPropertyName("data")]
    public JsonElement? DataRaw { get; set; }

    [JsonIgnore]
    public List<JsonElement>? Data
    {
        get => DataRaw is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : null;
        set => DataRaw = value is null ? null : JsonSerializer.SerializeToElement(value);
    }

    public A2AException ToException()
    {
        // Prefer the ErrorInfo reason (it can express codes JSON-RPC has no number for, e.g. UNAUTHENTICATED).
        A2AErrorCode code;
        if (A2AErrors.TryGetCodeFromDetails(Data, out var fromDetails)) code = fromDetails;
        else if (Code <= -32000 && Enum.IsDefined(typeof(A2AErrorCode), Code)) code = (A2AErrorCode)Code;
        else code = A2AErrorCode.Internal;
        return new A2AException(code, Message) { Details = Data };
    }
}
