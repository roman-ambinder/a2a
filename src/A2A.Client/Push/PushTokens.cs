using System.Security.Cryptography;
using A2A.Signing;

namespace A2A.Client.Push;

/// <summary>Notification tokens for webhook registrations (spec §13.2: unique, single-purpose tokens).</summary>
public static class PushTokens
{
    /// <summary>The number of random bytes in a token from <see cref="Create" />.</summary>
    public const int ByteLength = 32;

    /// <summary>
    ///     A new random token: <see cref="ByteLength" /> bytes from a cryptographic random number generator, base64url
    ///     encoded (43 characters). Use one per push config, and keep it as a secret.
    /// </summary>
    public static string Create()
    {
        var bytes = new byte[ByteLength];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Base64Url.Encode(bytes);
    }
}
