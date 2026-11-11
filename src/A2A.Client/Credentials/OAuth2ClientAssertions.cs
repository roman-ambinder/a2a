using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using A2A.Signing;

namespace A2A.Client.Credentials;

/// <summary>What a client assertion is for: the client it proves and the token endpoint it is sent to.</summary>
public sealed class OAuth2ClientAssertionContext
{
    /// <summary>Creates a context.</summary>
    public OAuth2ClientAssertionContext(string clientId, Uri tokenEndpoint)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        ClientId = clientId;
        TokenEndpoint = tokenEndpoint;
    }

    /// <summary>The OAuth2 client id.</summary>
    public string ClientId { get; }

    /// <summary>The token endpoint the assertion is sent to. A <c>private_key_jwt</c> uses it as its audience.</summary>
    public Uri TokenEndpoint { get; }
}

/// <summary>
///     Ready-made values for <see cref="OAuth2ClientCredentialsOptions.ClientAssertion" />: a token file that the
///     platform rotates (Kubernetes, Azure workload identity), or a <c>private_key_jwt</c> signed with your own key.
/// </summary>
public static class OAuth2ClientAssertions
{
    private const int MaxFileBytes = 64 * 1024;
    private static readonly TimeSpan DefaultJwtLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxJwtLifetime = TimeSpan.FromHours(1);

    /// <summary>
    ///     Reads the assertion from a file on every token request, so a token the platform rotates is picked up. White space
    ///     around the token is removed. Use this for a Kubernetes projected service account token, or for Azure workload
    ///     identity, whose token file is named by <c>AZURE_FEDERATED_TOKEN_FILE</c>. A file larger than 64 KiB fails the
    ///     token request.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="path" /> is empty.</exception>
    public static Func<OAuth2ClientAssertionContext, CancellationToken, ValueTask<string>> FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return async (_, cancellationToken) =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, true);
            var buffer = new byte[MaxFileBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
#if NETSTANDARD2_0
                var n = await stream.ReadAsync(buffer, read, buffer.Length - read, cancellationToken).ConfigureAwait(false);
#else
                var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
#endif
                if (n == 0) break;
                read += n;
            }

            if (read > MaxFileBytes)
                throw new InvalidDataException($"The assertion file is larger than {MaxFileBytes} bytes.");
            return Encoding.UTF8.GetString(buffer, 0, read).Trim();
        };
    }

    /// <summary>
    ///     Signs a new <c>private_key_jwt</c> (RFC 7523 section 3) for every token request. Claims: <c>iss</c> and
    ///     <c>sub</c> are the client id, <c>aud</c> is the token endpoint, <c>jti</c> is random, and <c>iat</c>, <c>nbf</c>
    ///     and <c>exp</c> bound it in time. The header carries <c>alg</c>, <c>typ</c> (<c>JWT</c>) and <c>kid</c> from
    ///     <paramref name="key" />. Register the public key (<see cref="AgentCardSigningKey.ToPublicJwk" />) with the identity
    ///     provider.
    /// </summary>
    /// <param name="key">The private key, its id and its algorithm. Only asymmetric algorithms are supported.</param>
    /// <param name="lifetime">How long each assertion is valid. Defaults to 5 minutes. At most 1 hour.</param>
    /// <param name="timeProvider">The clock, for tests. Defaults to <see cref="TimeProvider.System" />.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime" /> is not positive or longer than 1 hour.</exception>
    public static Func<OAuth2ClientAssertionContext, CancellationToken, ValueTask<string>> PrivateKeyJwt(
        AgentCardSigningKey key, TimeSpan? lifetime = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var life = lifetime ?? DefaultJwtLifetime;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(life, TimeSpan.Zero, nameof(lifetime));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(life, MaxJwtLifetime, nameof(lifetime));
        var time = timeProvider ?? TimeProvider.System;
        var header = Base64Url.Encode(Json(w =>
        {
            w.WriteString("alg", key.Algorithm);
            w.WriteString("typ", "JWT");
            w.WriteString("kid", key.KeyId);
        }));

        return (context, _) =>
        {
            var now = time.GetUtcNow().ToUnixTimeSeconds();
            var jti = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(jti);
            var payload = Base64Url.Encode(Json(w =>
            {
                w.WriteString("iss", context.ClientId);
                w.WriteString("sub", context.ClientId);
                w.WriteString("aud", context.TokenEndpoint.AbsoluteUri);
                w.WriteString("jti", Base64Url.Encode(jti));
                w.WriteNumber("iat", now);
                w.WriteNumber("nbf", now);
                w.WriteNumber("exp", now + (long)life.TotalSeconds);
            }));
            var signingInput = header + "." + payload;
            var signature = JwsAlgorithms.Sign(key.Algorithm, key.Key, Encoding.ASCII.GetBytes(signingInput));
            return new ValueTask<string>(signingInput + "." + Base64Url.Encode(signature));
        };
    }

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
