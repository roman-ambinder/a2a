namespace A2A.Server.Pipeline;

/// <summary>
/// Checks the protocol version, authentication and required extensions of an incoming call, and fills
/// <see cref="ServerCallContext.ActivatedExtensions"/>. Throws <see cref="A2AException"/> when a check fails.
/// </summary>
public sealed class CallValidator(A2AServerOptions options, IAgentCardProvider cards)
{

    /// <summary>Validates <paramref name="call"/> against the server options and the Agent Card's declared extensions.</summary>
    public void Validate(ServerCallContext call)
    {
        var version = string.IsNullOrWhiteSpace(call.ProtocolVersion)
            ? (options.TreatMissingVersionAsCurrent ? A2AProtocol.Version : A2AProtocol.ImplicitVersion)
            : MajorMinor(call.ProtocolVersion!);
        if (!options.SupportedVersions.Contains(version))
        {
            throw new A2AException(A2AErrorCode.VersionNotSupported,
                $"Protocol version '{version}' is not supported. Supported: {string.Join(", ", options.SupportedVersions)}. Send the {A2AProtocol.Headers.Version} header.")
            {
                Metadata = new() { ["requestedVersion"] = version, ["supportedVersions"] = string.Join(",", options.SupportedVersions) },
            };
        }

        // Backstop for hosts where the startup check could not run (or direct use of the handler): anonymous is opt-in.
        if (!options.AllowAnonymous && !call.IsAuthenticated)
            throw new A2AException(A2AErrorCode.Unauthenticated);

        var declared = cards.Get().Capabilities.Extensions ?? [];
        foreach (var ext in declared.Where(e => e.Required))
        {
            if (!call.RequestedExtensions.Contains(ext.Uri, StringComparer.Ordinal))
                throw new A2AException(A2AErrorCode.ExtensionSupportRequired, $"Extension '{ext.Uri}' is required.")
                {
                    Metadata = new() { ["extension"] = ext.Uri },
                };
        }
        foreach (var uri in call.RequestedExtensions)
        {
            if (declared.Any(e => e.Uri == uri)) call.ActivatedExtensions.Add(uri);
        }
    }

    private static string MajorMinor(string v)
    {
        var parts = v.Trim().Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : parts[0];
    }
}
