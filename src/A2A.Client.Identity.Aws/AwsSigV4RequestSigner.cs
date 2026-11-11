using A2A.Client.Credentials;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;

namespace A2A.Client.Identity.Aws;

/// <summary>Signs one <see cref="HttpRequestMessage" /> in place. Shared by the handler and the provider.</summary>
internal sealed class AwsSigV4RequestSigner
{
    // Stryker disable all : runs once per test process, so Stryker cannot test a mutant here (CONTRIBUTING.md, Mutation testing)
    private static readonly string[] SignerHeaders =
        ["Authorization", "X-Amz-Date", "X-Amz-Content-SHA256", "X-Amz-Security-Token"];
    // Stryker restore all

    private readonly AWSCredentials? _credentials;
    private readonly long _maxBodyBytes;
    private readonly string _region;
    private readonly string _service;
    private readonly TimeProvider _time;
    private readonly bool _unsignedPayload;
    private Task<AWSCredentials>? _defaultCredentials;

    public AwsSigV4RequestSigner(AwsSigV4Options options, bool requireAgentOrigin)
    {
        ArgumentNullException.ThrowIfNull(options);
        CheckName(options.Region, nameof(options.Region));
        CheckName(options.Service, nameof(options.Service));
        if (requireAgentOrigin && options.AgentOrigin is null)
            throw new ArgumentException("AgentOrigin is required: the handler signs only requests to this agent.",
                nameof(options));
        if (options.AgentOrigin is { } origin)
        {
            if (!origin.IsAbsoluteUri || (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp))
                throw new ArgumentException("AgentOrigin must be an absolute http or https URL.", nameof(options));
            if (options.RequireHttps && origin.Scheme != Uri.UriSchemeHttps && !origin.IsLoopback)
                throw new ArgumentException("AgentOrigin must use HTTPS. Loopback is exempt.", nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaxSignedBodyBytes, 0,
            nameof(options.MaxSignedBodyBytes));
        ArgumentNullException.ThrowIfNull(options.TimeProvider);

        _region = options.Region;
        _service = options.Service;
        _credentials = options.Credentials;
        _unsignedPayload = options.UnsignedPayload;
        _maxBodyBytes = options.MaxSignedBodyBytes;
        _time = options.TimeProvider;
        AgentOrigin = options.AgentOrigin;
    }

    public Uri? AgentOrigin { get; }

    public async ValueTask SignAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri
                  ?? throw new InvalidOperationException("The request has no RequestUri to sign.");
        var credentials = await GetCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var payloadHash = await PayloadHashAsync(request.Content, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        foreach (var name in SignerHeaders) request.Headers.Remove(name);
        var amzDate = now.UtcDateTime.ToString(SigV4.DateFormat, System.Globalization.CultureInfo.InvariantCulture);
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.TryAddWithoutValidation("X-Amz-Content-SHA256", payloadHash);
        if (credentials.UseToken)
            request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", credentials.Token);

        var headers = new List<KeyValuePair<string, string>>
        {
            new("host", request.Headers.Host ?? HostHeader(uri)),
            new("x-amz-date", amzDate),
            new("x-amz-content-sha256", payloadHash)
        };
        if (credentials.UseToken) headers.Add(new("x-amz-security-token", credentials.Token));
        if (request.Content?.Headers.ContentType is { } contentType) headers.Add(new("content-type", contentType.ToString()));

        var authorization = SigV4.Authorization(request.Method.Method, uri, headers, payloadHash,
            credentials.AccessKey, credentials.SecretKey, _region, _service, now);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    /// <summary>The Host header the transport sends: the dialed name, and the port when it is not the default.</summary>
    internal static string HostHeader(Uri uri)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 && !uri.IdnHost.StartsWith('[') ? $"[{uri.IdnHost}]" : uri.IdnHost;
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
    }

    private async ValueTask<string> PayloadHashAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null) return SigV4.EmptyPayloadHash;
        if (_unsignedPayload) return SigV4.UnsignedPayload;
        if (content is not ByteArrayContent)
        {
            // A stream can be read once. Buffer it (bounded) so the hash and the send see the same bytes.
            if (content.Headers.ContentLength > _maxBodyBytes) throw TooLarge();
            try
            {
                await content.LoadIntoBufferAsync(_maxBodyBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw TooLarge(ex);
            }
        }

        // CopyToAsync serializes the body again on every call. ReadAsStreamAsync would not do: HttpContent caches that
        // stream, so a retry would hash a stream that was already read to its end.
        using var sha = System.Security.Cryptography.SHA256.Create();
        await using (var hashing = new System.Security.Cryptography.CryptoStream(Stream.Null, sha,
                         System.Security.Cryptography.CryptoStreamMode.Write, leaveOpen: true))
        {
            await content.CopyToAsync(hashing, cancellationToken).ConfigureAwait(false);
            await hashing.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
        }

        return SigV4.Hex(sha.Hash);
    }

    private A2AException TooLarge(Exception? inner = null)
    {
        return new A2AException(A2AErrorCode.InvalidRequest,
            $"The request body is larger than MaxSignedBodyBytes ({_maxBodyBytes} bytes), so it cannot be hashed for SigV4. " +
            "Raise MaxSignedBodyBytes, or set UnsignedPayload if the service accepts unsigned payloads.", inner);
    }

    private async ValueTask<ImmutableCredentials> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        ImmutableCredentials? credentials;
        try
        {
            var source = _credentials ?? await DefaultCredentialsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            credentials = await source.GetCredentialsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // AWS SDK messages can name profiles and files, so only the type is reported.
            throw new A2AException(A2AErrorCode.Unauthenticated,
                $"The AWS credentials could not be obtained ({ex.GetType().Name}).", ex);
        }

        if (credentials is null || string.IsNullOrEmpty(credentials.AccessKey) ||
            string.IsNullOrEmpty(credentials.SecretKey))
            throw new A2AException(A2AErrorCode.Unauthenticated,
                "The AWS credentials have no access key or secret key.");
        return credentials;
    }

    private Task<AWSCredentials> DefaultCredentialsAsync()
    {
        var existing = Volatile.Read(ref _defaultCredentials);
        if (existing is not null && !existing.IsFaulted && !existing.IsCanceled) return existing;
        var created = Task.Run(() => DefaultAWSCredentialsIdentityResolver.GetCredentials(null));
        // A failed lookup is not kept: the next request looks again.
        Interlocked.CompareExchange(ref _defaultCredentials, created, existing);
        return Volatile.Read(ref _defaultCredentials)!;
    }

    private static void CheckName(string value, string name)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"{name} is required.", name);
        if (!value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            throw new ArgumentException($"{name} may contain only lower-case letters, digits and '-'.", name);
    }

    internal static bool IsAgentOrigin(Uri? origin, Uri requestUri)
    {
        return origin is not null && A2ACredentialBinding.IsSameOrigin(origin, requestUri);
    }
}
