using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Json;

namespace A2A.JsonRpc;

/// <summary>JSON-RPC 2.0 request envelope.</summary>
public sealed class JsonRpcRequest
{
    /// <summary>Protocol marker. Always <c>"2.0"</c>.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    /// <summary>String, number, or null. Absent for notifications (not used by A2A).</summary>
    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    /// <summary>Name of the A2A operation to call; see <see cref="A2AProtocol.Methods" />.</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>Operation parameters, or null when the method takes none.</summary>
    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

/// <summary>JSON-RPC 2.0 response envelope. Exactly one of <see cref="Result" /> or <see cref="Error" /> is set.</summary>
public sealed class JsonRpcResponse
{
    /// <summary>Protocol marker. Always <c>"2.0"</c>.</summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    /// <summary>Echo of the request id. Serialized as null when unknown (e.g. parse errors).</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public JsonElement? Id { get; set; }

    /// <summary>The result of a successful call.</summary>
    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    /// <summary>The error of a failed call.</summary>
    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    /// <summary>Builds a success response for the request <paramref name="id" />.</summary>
    public static JsonRpcResponse Success(JsonElement? id, JsonElement result)
    {
        return new JsonRpcResponse { Id = id, Result = result };
    }

    /// <summary>Builds an error response for the request <paramref name="id" /> from an <see cref="A2AException" />.</summary>
    public static JsonRpcResponse Failure(JsonElement? id, A2AException ex)
    {
        return new JsonRpcResponse
        {
            Id = id,
            Error = new JsonRpcError
            {
                // Auth errors have no JSON-RPC number in the spec; send Internal and let the ErrorInfo reason carry the type.
                Code = ex.Code is A2AErrorCode.Unauthenticated or A2AErrorCode.PermissionDenied
                    ? (int)A2AErrorCode.Internal
                    : (int)ex.Code,
                Message = ex.Message,
                Data = A2AErrors.BuildDetails(ex)
            }
        };
    }
}

/// <summary>JSON-RPC 2.0 error object.</summary>
public sealed class JsonRpcError
{
    /// <summary>JSON-RPC error code.</summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>Human-readable error message.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>Array of ProtoJSON <c>Any</c> detail objects (spec §9.5). Older peers may send any JSON value.</summary>
    [JsonPropertyName("data")]
    public JsonElement? DataRaw { get; set; }

    /// <summary>The details as a list, or null when <c>data</c> is absent or is not an array.</summary>
    [JsonIgnore]
    public List<JsonElement>? Data
    {
        get => DataRaw is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : null;
        set => DataRaw = value is null ? null : A2AJson.SerializeToElement(value);
    }

    /// <summary>Converts the error into an <see cref="A2AException" />. The ErrorInfo reason wins over the numeric code.</summary>
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