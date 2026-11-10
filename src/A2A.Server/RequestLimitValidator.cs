namespace A2A.Server;

/// <summary>Enforces the request size limits of <see cref="A2ALimits" /> on incoming messages.</summary>
public static class RequestLimitValidator
{
    /// <summary>
    ///     Checks the part count and the size of each raw part.
    /// </summary>
    /// <exception cref="A2AException">
    ///     InvalidRequest for too many parts; PayloadTooLarge (HTTP 413) for a raw part over the
    ///     limit.
    /// </exception>
    public static void Validate(Message message, A2ALimits limits)
    {
        var parts = message.Parts;
        if (parts is null) return;
        if (parts.Count > limits.MaxPartsPerMessage)
            throw new A2AException(A2AErrorCode.InvalidRequest,
                $"A message may have at most {limits.MaxPartsPerMessage} parts");
        foreach (var part in parts)
            if (part is RawPart { Raw.Length: var length } && length > limits.MaxRawPartBytes)
                throw A2AException.PayloadTooLarge(limits.MaxRawPartBytes);
    }
}