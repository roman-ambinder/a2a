using Amazon.Runtime;

namespace A2A.Client.Identity.Aws;

/// <summary>
///     Settings for <see cref="AwsSigV4Handler" /> and <see cref="AwsSigV4CredentialProvider" />. They are copied at
///     construction.
/// </summary>
public sealed class AwsSigV4Options
{
    /// <summary>The AWS region of the agent's endpoint, for example <c>us-east-1</c>. Required.</summary>
    public string Region { get; set; } = "";

    /// <summary>
    ///     The signing name of the AWS service in front of the agent. Required. For example <c>execute-api</c> for Amazon
    ///     API Gateway, or <c>lambda</c> for a Lambda function URL.
    /// </summary>
    public string Service { get; set; } = "";

    /// <summary>
    ///     The agent that gets signed requests. Only its scheme, host and port count. Required for
    ///     <see cref="AwsSigV4Handler" />, which signs only requests to this origin. Optional for
    ///     <see cref="AwsSigV4CredentialProvider" />, which otherwise binds to the first card origin it accepts.
    /// </summary>
    public Uri? AgentOrigin { get; set; }

    /// <summary>
    ///     The AWS credentials. When null, the AWS SDK's default credential search is used
    ///     (<c>DefaultAWSCredentialsIdentityResolver</c>: environment variables, the "default" profile, EC2 instance
    ///     metadata). It is resolved on the first request, not here.
    /// </summary>
    public AWSCredentials? Credentials { get; set; }

    /// <summary>
    ///     When true, the body is not hashed: <c>x-amz-content-sha256</c> is <c>UNSIGNED-PAYLOAD</c>. Set it only for a
    ///     service that accepts unsigned payloads. Defaults to false.
    /// </summary>
    public bool UnsignedPayload { get; set; }

    /// <summary>
    ///     The largest body that is buffered to be hashed, when the body cannot be read twice (a stream). A larger body fails
    ///     before it is sent. The A2A client's own request bodies are in memory already and are not limited by this.
    ///     Defaults to 10 MiB.
    /// </summary>
    public long MaxSignedBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>When true (the default), <see cref="AgentOrigin" /> must use HTTPS. Loopback addresses are always allowed.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    ///     The card scheme that <see cref="AwsSigV4CredentialProvider" /> handles. Required for the provider: no A2A
    ///     scheme type means SigV4, so the agent and the caller agree on a name. Not used by the handler.
    /// </summary>
    public string? SchemeName { get; set; }

    /// <summary>The clock that dates each signature, for tests. Defaults to <see cref="System.TimeProvider.System" />.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
