# RomanAmbinder.A2A.Client.Identity.Aws

AWS identity for the A2A (Agent2Agent) client. Signs requests with AWS Signature Version 4, to call an agent behind
AWS IAM: Amazon API Gateway with IAM authorization, or a Lambda function URL with `AWS_IAM` auth.

```csharp
// dotnet add package RomanAmbinder.A2A.Client.Identity.Aws
var agentUrl = new Uri("https://abc123.execute-api.us-east-1.amazonaws.com/prod/");
var sigV4 = new AwsSigV4Handler(new AwsSigV4Options
{
    Region = "us-east-1",
    Service = "execute-api",   // "lambda" for a Lambda function URL
    AgentOrigin = agentUrl,
}, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

var client = await A2AClientFactory.CreateAsync(agentUrl, new HttpClient(sigV4));
```

With `AddA2AClient`: `.AddHttpMessageHandler(() => new AwsSigV4Handler(options))`.

- `AwsSigV4Handler` signs every request to `AgentOrigin`, the card fetch included, and lets requests to other origins
  through unsigned. Use it when the card declares no scheme, which is usual: the AWS front door checks the signature,
  not the agent.
- `AwsSigV4CredentialProvider` signs when the card asks for the scheme named `SchemeName`. One provider per agent.
- Credentials: any `AWSCredentials`, or null for the AWS SDK's default credential search, resolved on first use.
- Bodies are hashed. A streamed body is buffered up to `MaxSignedBodyBytes` (10 MiB) first. `UnsignedPayload = true`
  sends `UNSIGNED-PAYLOAD`, for services that accept it.
- Signing adds `Authorization`, `x-amz-date`, `x-amz-content-sha256` and `x-amz-security-token`. The URI is not
  changed. The inner handler must not follow redirects.
- Depends on `AWSSDK.Core` for credentials only; the signer is in this package. Targets .NET 10.

Guide: [Identity providers](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/identity-providers.md#aws-iam-sigv4).
Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a).
