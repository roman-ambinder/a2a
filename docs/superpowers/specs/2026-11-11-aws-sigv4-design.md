# AWS SigV4: call agents behind AWS IAM

Date: 2026-11-11. Status: decided with the user (option D of `2026-11-10-identity-providers-design.md`, section 7).
Scope: a new package `src/A2A.Client.Identity.Aws`, tests, docs, snippets and a sample step. No server code. The S37 rules R1 to R6 (`2026-11-08-s37-credential-providers-design.md`) stay in force.

## 1. Problem

An agent behind an AWS service that checks IAM (Amazon API Gateway with IAM authorization, a Lambda function URL with `AWS_IAM` auth) accepts only requests signed with Signature Version 4. The client has no way to sign. A host must write the whole SigV4 algorithm itself.

## 2. The three open questions, decided

**When to sign.** No A2A security scheme describes SigV4, and the card of an agent behind IAM often declares no requirement at all: the agent does not see the signature, the AWS front door checks it. So the card cannot decide. Two entry points:

- D1. `AwsSigV4Handler`, a `DelegatingHandler`. The host opts in per agent: it names `AgentOrigin`, and the handler signs every request to that origin, the card fetch included (the front door protects it too). It does not depend on the card's schemes. This is the main path.
- D2. `AwsSigV4CredentialProvider`, an `IA2ACredentialProvider`, for a card that declares a scheme for SigV4 under a name the host and the agent agree on. `SchemeName` is required: no scheme type means SigV4 by itself.

**The body.** SigV4 hashes the body (`x-amz-content-sha256`). The A2A client sends `ByteArrayContent`, which can be read again, so retries and resumes hash it again with no copy.

- D3. Content that cannot be read again (the file uploader's stream) is buffered up to `MaxSignedBodyBytes` (default 10 MiB, the server's default body limit) and then hashed. A larger body fails before anything is sent, with `InvalidRequest`, naming `UnsignedPayload`.
- D4. `UnsignedPayload = true` sends `UNSIGNED-PAYLOAD` instead of the hash. Only for services that accept it.

**The URI.** D5. Signing adds headers only: `Authorization`, `x-amz-date`, `x-amz-content-sha256`, and `x-amz-security-token` for temporary credentials. `RequestUri` is never changed (the provider rule holds).

## 3. API

```csharp
public sealed class AwsSigV4Options
{
    public string Region { get; set; } = "";        // required, e.g. "us-east-1"
    public string Service { get; set; } = "";       // required, e.g. "execute-api", "lambda"
    public Uri? AgentOrigin { get; set; }           // required for the handler; optional for the provider
    public AWSCredentials? Credentials { get; set; } // null: the default AWS credential chain
    public bool UnsignedPayload { get; set; }
    public long MaxSignedBodyBytes { get; set; } = 10 * 1024 * 1024;
    public bool RequireHttps { get; set; } = true;
    public string? SchemeName { get; set; }         // required for the provider
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

public sealed class AwsSigV4Handler : DelegatingHandler
{
    public AwsSigV4Handler(AwsSigV4Options options);                               // for IHttpClientFactory
    public AwsSigV4Handler(AwsSigV4Options options, HttpMessageHandler innerHandler);
}

public sealed class AwsSigV4CredentialProvider : IA2ACredentialProvider
{
    public AwsSigV4CredentialProvider(AwsSigV4Options options);
}
```

## 4. Rules

- S1. The handler signs only requests whose origin equals `AgentOrigin` (`A2ACredentialBinding.IsSameOrigin`). Any other request passes through unsigned: a request elsewhere (an artifact download link, for example) gets no signature for another host.
- S2. `AgentOrigin` must be HTTPS unless it is loopback or `RequireHttps` is false.
- S3. No redirects. The constructor with an inner handler refuses a chain that follows redirects (`A2ACredentialHandler.EnsureNoAutoRedirect`). The constructor without one is for `IHttpClientFactory`; `AddA2AClient`'s primary handler does not follow redirects.
- S4. The provider binds to `AgentOrigin` when set, else to the first card origin it accepts, and refuses every other origin.
- S5. Credentials come from `Credentials.GetCredentialsAsync()` on every request. The AWS SDK caches and refreshes them. With no `Credentials`, the default chain (`DefaultAWSCredentialsIdentityResolver`) is resolved once, on first use, not in the constructor (it may call the instance metadata service).
- S6. Failures: a credential source that throws, or credentials with no access key or secret key, fail with `A2AException(Unauthenticated)` naming the exception type only. AWS SDK messages are not copied: they can name profiles and files.
- S7. Canonical request per the SigV4 rules: the path as sent, URI-encoded again (once only for service `s3`); query names and values decoded, encoded with the SigV4 rules (unreserved characters kept, everything else `%XX` upper case), sorted by name then value; signed headers `content-type` (when there is a body), `host`, `x-amz-content-sha256`, `x-amz-date`, `x-amz-security-token` (when present), values trimmed with inner runs of spaces collapsed. Headers the signer sets are removed first, so a retry is signed afresh.
- S8. `Host` is the name the transport dials: `Uri.IdnHost`, plus `:port` when not the default. An explicit `Host` header wins.

## 5. Package

`A2A.Client.Identity.Aws` (NuGet `RomanAmbinder.A2A.Client.Identity.Aws`), net10.0. Depends on `A2A.Client` and `AWSSDK.Core` 4.0.102.19 (the version `AWSSDK.S3` already brings). The signer is written here, not taken from the SDK: the SDK's signer works on its own request model, not on `HttpRequestMessage`.

## 6. Tests that prove it

- Reference signatures from botocore 1.44.0 (`SigV4Auth`, fixed time and keys): AWS's documented IAM example, a JSON-RPC POST, the same with a session token, a GET with port, encoded path and repeated query keys, special characters, and `UNSIGNED-PAYLOAD`. The C# signer must produce the same `Authorization` value.
- Canonicalization of a query that is not in canonical form (`%7E`, lower-case hex, a name with no value).
- Handler: signs only the agent origin; other origins unsigned; HTTPS rule; refuses a redirecting inner handler; a retry is re-signed, not double-signed; non-rereadable content is buffered and hashed; too-large body fails before sending; `UnsignedPayload`; credential failures; the default chain is not touched when `Credentials` is set.
- Provider: `SchemeName` matching, origin binding, signs in `ApplyAsync`.
- End to end: `A2AClientFactory.CreateAsync` over the handler against an in-process agent that rebuilds the signature from what it received.

## 7. Calls I made

1. Two entry points instead of one. The handler is what an IAM-protected agent needs, because its card usually declares nothing; the provider exists for cards that do declare a scheme.
2. Requests to other origins pass unsigned rather than fail. A signature is bound to its host, so this leaks nothing, and failing would break artifact downloads through the same client.
3. Query values are decoded and encoded again (as the SigV4 rules say), not passed through as sent (as botocore does). The two agree on canonical input; on other input the rules are what AWS checks.
4. Errors do not include AWS SDK messages (S6), unlike the Azure provider, which includes cleaned Azure messages.
5. No DI helper: `AddHttpMessageHandler(() => new AwsSigV4Handler(options))` works on the `AddA2AClient` builder.
