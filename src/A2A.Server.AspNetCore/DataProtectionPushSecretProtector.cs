using A2A.Server.Push;
using Microsoft.AspNetCore.DataProtection;

namespace A2A.Server.AspNetCore;

/// <summary>
///     Protects push config secrets with ASP.NET Core Data Protection, so they survive restarts and are shared across
///     a key ring.
/// </summary>
public sealed class DataProtectionPushSecretProtector(IDataProtectionProvider provider) : IPushSecretProtector
{
    /// <summary>Data Protection purpose string.</summary>
    public const string Purpose = "A2A.PushSecrets.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        return _protector.Protect(plaintext);
    }

    /// <inheritdoc />
    public string Unprotect(string protectedValue)
    {
        return _protector.Unprotect(protectedValue);
    }
}