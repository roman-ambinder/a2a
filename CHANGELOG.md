# Changelog

All notable changes to this project are listed here. Versions follow [Semantic Versioning](https://semver.org/); see the [versioning guide](docs/site/guides/versioning.md) for what is covered and what is experimental.

## Unreleased

### Authentication on the card

- New `AddBearerAuthentication()` and `AddCardAuthentication(cardSchemeName, scheme, authenticationScheme)` on the A2A
  builder: the scheme goes on the Agent Card (`securitySchemes`, and one more `securityRequirements` entry), so
  clients know what to send, and it is proved by the ASP.NET Core scheme you name (default `Bearer`, as `AddJwtBearer()`
  registers it). The app refuses to start when that scheme is not registered.
- Every A2A endpoint without authorization of its own now gets the policy `A2ACardSchemes`
  (`A2ACardAuthenticationExtensions.DefaultPolicyName`): the caller is authenticated by any declared scheme. API keys
  are one of them, so with `AddApiKeyAuthentication` and `AddBearerAuthentication` either a key or a token gets in.
  (`AddApiKeyAuthentication` used to apply the `A2AApiKey` policy; that policy still exists for endpoints that take
  only API keys.)
- Analyzer A2A0002 knows that `AddBearerAuthentication` and `AddCardAuthentication` protect the endpoints.
- The `a2a-agent` and `a2a-chat-agent` templates (without `--auth apikey`) call `AddBearerAuthentication()` outside
  Development: the card asks for a bearer token, and the app refuses to start until a scheme such as `AddJwtBearer()`
  is registered, instead of answering every call with a 500.

### Ease of use

- New `A2AClient.AskAsync(text)`: sends a message and returns the answer as text, the text of a direct reply or of the
  task's artifacts once the task completed (a task still running is polled). New `A2AClient.StreamTextAsync(text)`
  yields the text as it streams; a task snapshot does not repeat text already yielded. It stops when the task ends or
  needs input or authorization, even when the server keeps the stream open. Both throw the new
  `TaskNotCompletedException` (with the task and its state) when the task fails, is rejected or canceled, or needs input
  or authorization.
- New text and state helpers on the model: `Artifact.GetText()`, `AgentTask.GetText()` (text of all artifacts),
  `SendMessageResponse.GetText()`, `StreamResponse.GetText()` (the text an artifact chunk or a message carries) and
  `StreamResponse.State` (the state of a status update or a task snapshot, so one check sees the end of a task).
- New `TaskUpdater.OpenArtifact(name)` returns an `ArtifactWriter` that streams one artifact: the first `WriteAsync`
  creates it, later ones append, and `CompleteAsync` (or disposing the writer) sends the last chunk. A terminal state of
  the task completes the open writers first, so the artifact is sealed before the task ends. `samples/EchoAgent` uses it.
- Configuration: `services.AddA2A(builder.Configuration.GetSection("A2A"), a => ...)` and `services.ConfigureA2A(section)`
  read the Agent Card (name, description, version, provider, capabilities, modes, skills), `PublicBaseUrl`,
  `AgentCardMaxAge`, every `A2ALimits` and `StreamingOptions` value, the rate limit and the main task settings. No
  reflection, so it stays AOT-safe. An unknown key fails at startup with its path. Settings that weaken a secure default
  (`AllowAnonymous`, `TreatMissingVersionAsCurrent`, `IncludeExceptionDetails`, `ClientContextIdPolicy`,
  `RateLimit:Enabled`) are refused there and must be set in code. Code runs after configuration and wins.
- New `A2AServerBuilder.UseDevelopmentDefaults()`: for local work, lets anonymous callers in, accepts calls without the
  `A2A-Version` header and puts exception text in errors. Rate limiting and the limits stay on. The app refuses to start
  in any environment other than Development.
- The served Agent Card turns an unset `Capabilities.Streaming` into `true`: every executor can stream, so forgetting
  the flag no longer makes streaming calls fail with `UnsupportedOperation`. An explicit `false` still refuses them. The
  host's card instance is not changed.
- New `A2AClientFactory.CreateForEndpoint(endpoint, httpClient, binding)`: a client for an endpoint you already know (an
  agent in the same process, an address from your configuration), without fetching the card. It checks no capabilities
  and verifies no card signature. The in-process samples use it instead of building a transport by hand.
- New `AgentCard.Create(name, description, version = "1.0.0")` and chainable `AgentCard.AddSkill(...)` (refuses a duplicate
  skill id).
- New template pack `RomanAmbinder.A2A.Templates`: `dotnet new a2a-agent`, `a2a-client` and `a2a-chat-agent`. The agents
  read their card from `appsettings.json`, use `UseDevelopmentDefaults()` in Development and require authorization
  elsewhere. The pack stamps its own version into the package references. `src/A2A.Templates/test-templates.sh` (run in
  CI after packing) builds each template against the packed SDK and runs the client against both agents.
- Analyzers in `RomanAmbinder.A2A.Server.AspNetCore` report hosting mistakes at build time: A2A0001 (`UseA2A()` never
  called), A2A0002 (endpoints mapped without authorization), A2A0003 (`UseA2A()` in the wrong place). Code fixes add
  `app.UseA2A()` before `UseAuthorization()` and `.RequireAuthorization()` to the mapping. They are warnings, and the
  startup checks are unchanged.
- README: a "Which packages do I need?" table.
- Server API-key authentication: `AddApiKeyAuthentication(o => o.Add("billing", hash))` on the A2A builder. It registers
  an authentication scheme for `X-Api-Key`, gives each key a caller id (task owner `apikey:<id>`, so callers never share
  tasks, also not with users of another scheme), declares the scheme on the Agent Card, can limit a key to tenants
  (through the new `a2a:tenant` claim, below), and puts the `A2AApiKey` policy on every A2A endpoint without authorization of its own (endpoints with
  their own policy keep it). Keys are registered by their SHA-256 hash only. New `A2AApiKey` makes keys (`a2a_` and 32
  random bytes; other forms are refused) and hashes them. Keys come from code, the `A2A:ApiKeys` configuration section
  (reloaded without a restart; a broken reload keeps the previous keys; a plain `Key` entry is refused) or a custom
  `IApiKeyStore`. New CLI command `a2a keys new` / `a2a keys hash` (offline). `samples/EchoAgent` uses it instead of a
  hand-written handler and takes `--EchoAgent:ApiKeyHash`. The agent templates take `--auth apikey`; the client template
  sends `A2A_API_KEY`. Analyzer A2A0002 knows that `AddApiKeyAuthentication` protects the endpoints.
- New claim type `A2AClaimTypes.Tenant` (`a2a:tenant`): a caller with one or more of these claims may call only those
  tenants. The server checks it in its authorization step, on every binding, before the host's
  `A2AAuthorizationHandler`: a call to another tenant is refused like any other denial (a task-scoped call looks like a
  missing task, the rest is `PermissionDenied` with `permission` `tenant`), logged as a security event and written to
  the audit trail with the reason `TenantNotAllowed`. API keys registered with tenants set it; other schemes can set it
  in a claims transformation.
- `samples/TenantAgent`, `samples/MultiInstance` and `samples/ArtifactStorage` use `AddApiKeyAuthentication` instead
  of hand-written handlers. TenantAgent and ArtifactStorage make their keys at startup; TenantAgent limits each key to
  its tenant. MultiInstance takes `ApiKeyHash` (a public demo key in `samples/Commons`, for the Docker demo only).
- `samples/EchoClient` sends its API key with `AddApiKey` instead of a hand-written handler, and uses the new helpers.

### Identity providers

- `OAuth2ClientCredentialsOptions.RequireScopes`: when true, a provider without `Scopes` is refused at construction, so
  a card can never choose the scopes of the token. Default false: nothing changes for existing setups.
- `OAuth2ClientCredentialsOptions.Logger`: a provider with a shared `TokenEndpoint` and no `Scopes` logs warning 190 (each
  card then chooses the scopes of the token it receives). `AddOAuth2ClientCredentials` sets the logger from the app's
  `ILoggerFactory`.
- `OAuth2ClientCredentialsProvider` can log in without a secret. New `OAuth2ClientCredentialsOptions.ClientAssertion` sends a
  signed JWT as `client_assertion` (RFC 7523) instead of `client_secret`. New `OAuth2ClientAssertions.FromFile` reads a
  token file the platform rotates (Kubernetes service account tokens, Azure workload identity), and
  `OAuth2ClientAssertions.PrivateKeyJwt` signs a `private_key_jwt` with your own key. `ClientSecret` and `ClientAssertion`
  cannot both be set. `client_assertion` and `client_assertion_type` are refused in `AdditionalParameters`.
- New `OAuth2ClientAssertions.CertificateJwt`: a client assertion signed with an X.509 certificate's private key, naming
  the certificate by thumbprint (`x5t#S256` by default, or `x5t`) instead of `kid`, for identity providers that register
  certificates, such as Entra ID certificate credentials. `PS256` by default; RSA and EC certificates.
- New package `RomanAmbinder.A2A.Client.Identity.Azure`: `AzureTokenCredentialProvider` gets tokens from any Azure.Core
  `TokenCredential` (managed identity, workload identity, `DefaultAzureCredential`, ...). Scopes are required and set by the
  host, never taken from the card. One provider binds to one agent origin. Tokens are cached and refreshed before expiry.
- New package `RomanAmbinder.A2A.Client.Identity.Aws`: call agents behind AWS IAM (API Gateway with IAM authorization,
  Lambda function URLs) with AWS Signature Version 4. `AwsSigV4Handler` signs every request to one agent origin, the card
  fetch included, for agents whose card declares no scheme. `AwsSigV4CredentialProvider` signs when the card asks for a
  scheme you name. Credentials come from any `AWSCredentials`, or the AWS SDK's default search. Bodies are hashed (streams
  are buffered up to `MaxSignedBodyBytes`), or sent as `UNSIGNED-PAYLOAD` when asked. Signatures are checked against
  botocore in the tests.
- DI helpers: `AddAzureTokenCredential` and `AddAwsSigV4` on the `AddA2AClient` builder. `AgentOrigin` defaults to the
  client's `AgentUrl`. Both identity packages now depend on `A2A.Client.DependencyInjection`. New public
  `IHttpClientBuilder.GetA2AClientName()` gives other packages the A2A name of a client builder.
- The netstandard2.0 smoke test (`tests/A2A.NetStandard.Smoke`, run in CI) now checks `OAuth2ClientAssertions.FromFile`
  and `CertificateJwt` on the netstandard2.0 build.
- New guide: [Identity providers](docs/site/guides/identity-providers.md). Entra ID on the server (JWT bearer and an owner
  id from `tid` and `oid`), which client option fits which platform, and AWS SigV4.

### Protocol and bindings

- Validation errors of request fields carry a `google.rpc.BadRequest` detail with the field and the reason (spec v1.0.1
  §9.5, §11.6), after the `ErrorInfo`, on all three bindings. The error message is unchanged. New
  `A2AException.InvalidField(field, description)` builds one; `A2AErrors.BadRequest` and `A2AErrors.BadRequestType`
  build the detail entry. The gRPC client reads `BadRequest` back into `A2AException.Details`.
- Each error detail object must carry an `@type` (spec v1.0.1 §3.3.2). An entry of `A2AException.Details` that is not an
  object with a non-empty `@type` is no longer sent. New `A2AErrors.HasType`.
- The extended Agent Card over HTTP+JSON (`GET /extendedAgentCard`) has caching headers (spec §13.3):
  `Cache-Control: private, no-cache`, `Vary: Authorization, Cookie, A2A-Version` and an `ETag` of the served bytes. A
  matching `If-None-Match` gets 304, after authorization as before. JSON-RPC and gRPC are unchanged.
- The client sends `A2A-Version` as Major.Minor only (spec §3.6): a card interface that lists `1.0.2`, or
  `A2AClientOptions.ProtocolVersion = "1.0.2"`, is sent as `1.0`. New `A2AProtocol.ToMajorMinor`.
- `PermissionDenied` says which permission is missing (spec §3.3.2): ErrorInfo metadata `permission` (`operation`,
  `skill` or `policy`) and `operation` (the server's operation name), and a message that names the operation. It never
  repeats a value the caller chose, and an unknown skill still gets the same error as a denied one.

- Server: new opt-in `RequireAgentCardSchemes(map)` for `MapA2A` and `MapA2AGrpc` (spec §7.4, §13.3). It maps each card
  security scheme to an ASP.NET Core authentication scheme and requires that one of the card's `securityRequirements` is
  met; an unmapped card scheme is never met, and a card without requirements allows anonymous callers. Scopes are not
  checked.
- Client: new `PushNotificationReceiver.ExpectedTask` (spec §4.3.3). When set, a delivery for a task the client did not
  create, or without a task id, is refused with the new `PushHandleOutcome.UnexpectedTask` (404) before the handler runs.
  `PushTaskTracker.IsTrackedAsync` fits it. Without it nothing changes.
- Client: a `jku` key set is cached for the lifetime its response gives (`no-store`, `no-cache`, `max-age` less `Age`,
  `Expires`), at most the previous 10 minutes, so a key the publisher removes stops verifying cards sooner (spec §8.4.3).
  New `IJwksLifetimeFetcher` and `JwksFetchResult`; `GuardedJwksFetcher` implements it.
- Client: the JSON-RPC and HTTP+JSON clients fill `A2AException.Metadata` from the received `ErrorInfo`, as the gRPC
  client already did. New `A2AErrors.GetErrorInfoMetadata`.

- Server: a startup check of the Agent Card's interfaces (spec §5.2, §8.3.1, §13.4). After the app has started it
  warns, and never stops the app, when a mapped binding is not on the host card, when a binding is mapped at a tenant
  pattern (never listed automatically), and, outside Development, when an interface is on a local address
  (`http://localhost` when `PublicBaseUrl` is not set), on plain HTTP, or when the server has no HTTPS address and
  `PublicBaseUrl` is not https. Log events 702-706.
- Tests: `ProtoModelDriftTests` compares the hand-written JSON model with the types generated from `docs/a2a.proto`
  (spec §1.4): messages, fields, every case of the union messages (round-tripped through the model) and enum values.

- Client webhooks (spec §13.2): `RegisterPushAsync` gives a config with neither a token nor authentication its own random
  token (new `PushTokens.Create`, 32 bytes, base64url) and returns it in the config even though servers redact it. New
  `A2AClient.RotatePushTokenAsync` registers a new token, then deletes the old config. New opt-in
  `A2AClientOptions.RequireHttpsWebhooks` refuses non-https webhook URLs (loopback allowed). New opt-in
  `PushNotificationReceiver.MaxDeliveriesPerMinute`: over the cap, `HandleAsync` answers the new
  `PushHandleOutcome.RateLimited` (429) with `PushHandleResult.RetryAfter`; only authenticated deliveries count.

- Agent Card caching (spec §8.6): the public card sends `Last-Modified` (the time this process first served that content;
  `If-Modified-Since` does not give 304, the entity tag decides). The client card cache follows RFC 9111 as a private
  cache: `no-store` is no longer kept (it was stored and revalidated), `Vary: *` and `Pragma: no-cache` revalidate,
  freshness counts `Expires` minus `Date` and subtracts the response's age (`Age` or the time since `Date`), and
  `Last-Modified` is sent back as `If-Modified-Since` when there is no entity tag. TCK `CARD-CACHE-003` now passes.

- gRPC authentication challenge (spec §3.3.2, §7.4): an `UNAUTHENTICATED` status from the SDK now carries a
  `www-authenticate` metadata entry naming the scheme, the same one the HTTP bindings send as `WWW-Authenticate` (the first
  HTTP auth scheme of the tenant's card, else `Bearer`), on unary and streaming calls and on extension operations. New
  `GrpcErrors.AuthChallengeKey`.
- Required card arrays (spec §5.7): outside of `supportedInterfaces`, which was already refused when empty, an empty
  `defaultInputModes`, `defaultOutputModes`, `skills` or skill `tags` now logs a warning at startup (event 708). The card is
  still served, and cards are still read leniently; documented in spec-decisions.
- v0.3 deprecation warnings (spec A.2): each field the v0.3 conversion drops is now logged once at Warning (was Debug), the
  server warns once per v0.3 operation and binding it answers (event 1002), and the compat client warns once per v0.3 agent
  URL it talks to (event 1003). New `V03CompatClientFactory.UseLogger` gives the client a logger.
- Unverified Agent Cards (spec §8.4.3): `A2ACardResolver` logs a warning (event 102) once per origin when it accepts an
  `Unsigned` or `Unverified` card, because no trusted keys or `jku` hosts are configured or `RequireSignature` is false.
  The default is unchanged: such cards are still accepted when no trust is configured.
- Required fields in agent responses (spec §5.7): the client now rejects a response that lacks a REQUIRED field with
  `InvalidAgentResponse`: a task without `id` or status `state`, a stream update without `taskId` or `contextId`, a message
  without `messageId`, `role` or parts, an artifact without `artifactId` or parts (append chunks and stored artifacts may
  be empty). Applies to SendMessage, GetTask, ListTasks, CancelTask and every stream event. New
  `A2AClientOptions.ValidateResponses` (default true) turns it off; new `A2AValidator.ValidateResponse` overloads do the
  checks. A fetched Agent Card that lacks REQUIRED fields is still used, and the resolver logs them once per origin
  (event 103).

### Tests and CI

- `CardTrustTests.Card_change_raises_event_and_strict_throws` uses its own origin: the `a2a.card.changed` counter is
  process-wide, and another card test running in parallel could count a second change for the shared origin.
- Coverage is now a ratchet. The new `coverage.yml` workflow (after each merge to `main`, by hand, and on pull requests
  that change the coverage setup) checks line coverage per package against `.github/coverage-baseline.json` and fails
  when a package drops more than 0.5 points or reports no data. It replaces the report-only `coverage` job in `ci.yml`
  and does not block a release.
- The store tests (`tests/A2A.Stores.Tests`: Postgres, Redis, S3 on MinIO, Azure Blob on Azurite) now count toward
  coverage, so the store packages have a baseline too.
- New weekly `mutation.yml` workflow: Stryker.NET mutation testing of the SSRF guard, Agent Card signing and
  canonicalization, webhook signatures and AWS SigV4 signing. Report only. See [CONTRIBUTING](CONTRIBUTING.md#mutation-testing).
- The AOT publish job runs in the `mcr.microsoft.com/dotnet/sdk:10.0-noble-aot` image, which has clang and zlib, instead
  of installing them with `apt-get` on every run. An outage of Ubuntu's package mirrors no longer fails CI.
- New tests for `SafeHttp` (URL policy messages and options, redirects, error statuses, body limits with and without
  `Content-Length`) and `AgentCardCanonicalizer` (a card using every v1.0 field is signed whole, prune paths, bracket
  paths, case variants). Mutation score of `SafeHttp.cs`: 30% to 95%.
- New tests for `AgentCardVerifier` (each signature-entry and header check with its exact reason, several signatures,
  case-variant members, trusted keys pinned to another `alg`, unusable trusted keys, every `jku` refusal, failed and
  timed-out fetches, caller cancellation, key set caching) and `Jwk` / `Base64Url` (EC curves and RSA, import round
  trips, every refusal). Mutation scores: `AgentCardVerifier.cs` 63% to 85%, `Jwk.cs` 52% to 70%.
- New tests for `PrivateAddressGuard` (every IPv4 and IPv6 range at its edges, NAT64, 6to4 and IPv4-compatible
  addresses and their near misses, `InNetwork` prefixes inside a byte, ambiguous numeric hosts, the resolve-time and
  connect-time checks and their messages) and `AwsSigV4Handler` (both constructors check the options). Mutation score of
  `PrivateAddressGuard.cs`: 65% to 92%.
- New tests for `AgentCardSigner` and `JwsAlgorithms` (key and algorithm checks, the protected header, every supported
  algorithm signing and verifying, EC signature sizes, PKCS#1 vs PSS, wrong keys, `KeyMatches`) and `GuardedJwksFetcher`
  (status codes, the 256 KiB cap with and without `Content-Length`, read errors, `Cache-Control`/`Age`/`Expires`
  lifetimes, the timeout). Mutation scores: `AgentCardSigner.cs` 84% to 90%, `GuardedJwksFetcher.cs` 70% to 93%.
- New tests for `AwsSigV4RequestSigner` (option checks and messages, agent origin matching, body hashing and
  `UNSIGNED-PAYLOAD`, the signed-body limit, non-seekable bodies, replaced signing headers, credential errors) and
  `AwsSigV4CredentialProvider` (argument checks, one agent origin per provider). Mutation score of
  `AwsSigV4CredentialProvider.cs`: 59% to 94%. The Stryker score of `AwsSigV4RequestSigner.cs` is not reliable (see
  [CONTRIBUTING](CONTRIBUTING.md#mutation-testing)); its earlier survivors were checked by hand instead.
- Mutation scores are now trustworthy for code that builds static data: that code is excluded with `// Stryker
  disable all` comments (it runs once per test process, so Stryker could not test its mutants, and a mutant breaking it
  made every later mutant look killed), and the configs use `coverage-analysis: perTest`. Scores with the fix:
  `AgentCardCanonicalizer.cs` 94%, `AgentCardSigner.cs` 100%, `AwsSigV4RequestSigner.cs` 86%.
- Faster CI: test hosts in `A2A.Tests` and `A2A.V0_3Compat.Tests` no longer wait for a 20 s shutdown drain
  (`A2A.Tests` 4m50s to 45 s, `A2A.V0_3Compat.Tests` 43 s to 3 s); `pack`, `benchmarks` and `aot-publish` run alongside
  `build-test`; `build-test` caches NuGet packages.
- Fewer fixed waits in tests: five tests now wait for the condition they check (a marker connection, an open stream,
  stored events, the clock, the health check) instead of sleeping, and an unbounded poll has a deadline. The 17
  remaining "nothing happens in N ms" checks need a production hook each; CONTRIBUTING does not list them yet.

### Fixes

- Server: a stream whose re-authorization (`StreamReauthInterval`) is denied now ends with the error a new request would
  get, `TaskNotFound` for a task-scoped stream (it does not confirm the task exists) or `PermissionDenied`, instead of
  ending quietly. The denial is logged and audited as before.
- Server: new `A2ALimits.StreamIdleTimeout` (default off). A stream that sent no event for that long ends normally, as at
  `MaxStreamDuration`, and clients resume with `Last-Event-ID`. Configuration key `A2A:Limits:StreamIdleTimeout`.
- gRPC: a request holding a `google.protobuf.Value` with no kind set is `INVALID_ARGUMENT`, not `INTERNAL`.
- gRPC client: a response the model cannot hold is `InvalidAgentResponse`, as over HTTP, not `InvalidParams`, and a
  malformed `Value` in a response no longer escapes as a raw `InvalidOperationException`.
- gRPC: a stream whose handler yields no event ends with `InvalidAgentResponse` ("Empty stream"), as on the HTTP bindings,
  instead of a successful empty stream.
- v0.3 compat: a rate limit rejection on a v0.3 HTTP+JSON route is written in the v0.3 error format (`{ code, message }`).
  An endpoint can now carry its own `IA2ARejectionWriter` in its metadata, which is asked before the registered ones.
- `A2AChatClient`: a task that completes with only a status message (an agent that ends with `CompleteAsync("text")`)
  returns that message, unary and streaming, as `AsAIFunction` does. With artifacts, the status message stays hidden.
- SSRF guard: IPv4-translated IPv6 addresses (`::ffff:0:a.b.c.d`, RFC 2765) are judged by their IPv4 address.
- Client: a request refused by the SSRF guard (`BlockedAddressException`, also when wrapped by the handler) is not retried.
- HTTP+JSON clients (v1.0 and v0.3): a task id, config id or tenant of `.` or `..` is refused with `InvalidParams` before
  sending. `System.Uri` removes dot segments, even escaped, so the request went to another path of the same origin.
- netstandard2.0: `A2AClientFactory.CreateAsync` with credential providers and no handler shares one `HttpClientHandler`
  instead of making one per call, as the .NET 10 build already did.
- Server: a context the orphan sweep removed while a new task in it was starting is put back when the task is stored, so
  the task never points at a missing context.
- Server: `ContinueTaskAsync` is refused (`AlreadyRunning`) while a follow-up's message is committed but its execution is
  not registered yet.
- Server: a push config created while its task is purged no longer outlives the task: the create answers `TaskNotFound`
  and the purge deletes configs again after the task row.
- Push outbox (in-memory and Postgres): a redriven dead letter waits while a later delivery of its task and config is in
  flight, so one config never has two deliveries out at once.
- Postgres: `PostgresMaintenance` also removes idempotency claims that were never finished within
  `IdempotencyClaimTimeout` (their instance crashed).
- S3: a PUT or CompleteMultipartUpload that fails after S3 applied it no longer leaves the object behind, as the Azure Blob
  store already did.
- Client: `PushNotificationReceiver.HandleAsync` with a signing secret de-duplicates on the signed `webhook-id`. It used the
  unsigned `X-A2A-Delivery-Id` first, so a replay within the signature tolerance that changed only that header ran the
  handler again. The SDK's sender puts the same value in both headers, so its deliveries keep the same id.
- Client: `A2ACardResolver` refuses an Agent Card served from another origin after a redirect (`InvalidAgentResponse`),
  instead of verifying, pinning and identifying it as the origin that was asked for. This matters for an `HttpClient` that
  follows redirects; a redirect within the same origin is still accepted.
- Server: a follow-up message and a 1.1 SendMessage with only client artifacts no longer ask the tenant's card provider
  while holding the task lock. A slow card provider stalled every task on the same lock stripe, across tenants.
- Server: a page token whose position is out of range gets InvalidParams instead of an internal error.
- Postgres: the idempotency store deletes a stale claim only if it is unchanged since it was read. A claim its owner renewed
  or completed in between was deleted, so a retry could run the request a second time.
- Postgres: a request waiting on an idempotency key held by another request stops polling the database when its caller is
  canceled.

### Documentation

- Spec decisions: the push delivery defaults said "10 attempts". The default is no limit on the count (`MaxAttempts`
  null); a delivery still failing 24 hours after it was queued is dead-lettered (`MaxDeliveryAge`).

### Performance

- Streaming many artifact chunks no longer copies the whole artifact for every chunk. When an agent sends chunks faster
  than they are stored, the server applies the waiting chunks to one copy of the task and stores them together. Each event
  keeps its own sequence number, so streams, resubscribe and push see no difference. A 1,000-chunk stream now allocates
  about 5.5 MB instead of 18.5 MB.
- New optional store interface `IBatchTaskCommit` (`TaskBatchCommit`, `BatchedEvent`, `BatchCommitResult`). The in-memory and
  Postgres task stores implement it. A store without it keeps working as before, one commit per event.
- New virtual `EventStore.ReadRangeAsync` reads only the events a stream is missing. The in-memory and Postgres event stores
  read just that range instead of every retained event. The in-memory event log is now a ring buffer.
- The server no longer reads the task back from the store before every event of a run, and an idle stream no longer
  creates a timer for every event.
- SSE frames are written straight into the response pipe. A JSON-RPC stream builds the start of its response envelope once
  (new `JsonRpcWriter.SuccessPrefix`) and copies each event's stored bytes after it. No envelope, frame or id string is
  made per event, and a stream has one keep-alive timer instead of one per event. Unary JSON responses are no longer
  copied into a new array. A 1,000-chunk stream now allocates about 4.4 MB.
- JSON reading and writing allocate less: union members, part members and enum names are matched on the raw bytes, and
  timestamps and enum names are written without a temporary string.
- The client reads stream events from their UTF-8 bytes: no string, no re-encoding and no `JsonDocument` per event, for
  JSON-RPC and HTTP+JSON. Typed JSON-RPC calls bind the result straight from the response body. A 1,000-chunk stream (server
  and client together) now allocates about 2.7 MB, down from 18.5 MB, and runs in about a third of the time.
- `A2AClientFactory.CreateAsync` with credential providers and no handler shares one connection pool instead of making a new
  one per call (which was never disposed). That pool, and the primary handler of `AddA2AClient`, recycle connections every
  2 minutes, so DNS changes are seen.
- A cached Agent Card is checked again on each `ResolveAsync` (as before) without parsing its text again.
- gRPC: stream events and SendMessage responses are mapped between the model and protobuf field by field instead of through
  ProtoJSON: about 10x less CPU and 15x less memory per message one way, 40x less CPU the other. Anything uncommon, and every
  error, still takes the ProtoJSON path, and tests check on a random corpus that both give the same result. The ProtoJSON
  path itself reuses its buffers.
- gRPC: clients made through `UseGrpc` / `A2AGrpcClientFactory` share one channel per address and channel options instead of
  opening a channel per client that was never disposed.
- `A2AProtocol.IsVersion11OrLater` parses the version in place instead of splitting it into new strings. The server calls it
  for every stream event since A2A 1.1, so this removes about 190 bytes per event.
- Agent Card verification is about 2.5x faster and allocates about 40% less: imported public keys are reused per trusted key
  (importing cost more than checking the signature), the signed bytes and the case-variant check come from one prune of
  the card instead of two, paths of pruned members are built only when reported, and base64url is encoded in place. The
  canonical bytes are unchanged. JCS sorts object members without LINQ, which also helps the idempotency hash of every
  SendMessage.

## 0.1.0

First public release of the .NET SDK for the Agent2Agent (A2A) protocol v1.0. Targets .NET 10. `A2A.Protocol` and `A2A.Client` also
target netstandard2.0. .NET 8 is not a target: its support ends on 10 November 2026.

### Postgres schema migrations

- `A2A.Server.Postgres` keeps its schema with numbered migrations, applied at startup in one transaction under an advisory lock. New: `PostgresSchema.ScriptFor(schema, fromVersion)` and `PostgresSchema.VerifySchemaAsync`. `PostgresSchema.Version` is now a property. With `CreateSchemaOnStartup = false` the app refuses to start when the schema version differs from the package.

### Packages

- 20 packages on NuGet, all with the ID prefix `RomanAmbinder.`, so they never clash with the official `A2A` packages.
  Assemblies and namespaces are `A2A.*`.
- Client: `A2A.Protocol`, `A2A.Security.Http`, `A2A.Client`, `A2A.Client.DependencyInjection`, `A2A.Client.Grpc`,
  `A2A.Client.Postgres`, `A2A.Client.V0_3Compat`.
- Server: `A2A.Server`, `A2A.Server.AspNetCore`, `A2A.Server.Grpc`, `A2A.Server.Postgres`, `A2A.Server.Redis`,
  `A2A.Server.TypedSkills`, `A2A.Server.Storage.S3`, `A2A.Server.Storage.AzureBlob`, `A2A.Server.V0_3Compat`.
- Shared: `A2A.Grpc` (gRPC types) and `A2A.V0_3Compat` (v0.3 codecs).
- Microsoft.Extensions.AI: `A2A.Extensions.AI`.
- Tool: `A2A.Cli` (the `a2a` command; install with `dotnet tool install -g`).
- Testing: `A2A.TestKit`, for agent authors. An in-process agent host, a scripted fake peer agent, assertions for task states
  and event order, a fake clock (`TestClock`) and a push receiver. It works with any test framework. It does not claim AOT
  compatibility and has no gRPC.
- Model Context Protocol: `A2A.Mcp`.
- Every package has a README, the Apache-2.0 license, SourceLink and a symbol package. The libraries are AOT compatible.
  (#4, #13)

### Command line tool

- `RomanAmbinder.A2A.Cli` is a dotnet tool with the command `a2a`: `card`, `send`, `stream`, `subscribe`, `get`, `list`,
  `cancel` and `download`. It uses JSON-RPC or HTTP+JSON, has no extra dependencies, and is AOT and trim friendly.
- Credentials come from options or from `A2A_TOKEN`, `A2A_API_KEY`, `A2A_CLIENT_ID` and `A2A_CLIENT_SECRET`. Secrets are
  never printed. Agent text is cleaned before it reaches the terminal. Exit codes are documented in the
  [guide](docs/site/guides/cli.md).
- `a2a download` checks the size and `sha256` of each file, writes to a temporary file first, and never lets an agent-chosen file
  name leave the output folder.

### Protocol and bindings

- JSON-RPC, HTTP+JSON and gRPC bindings, for both client and server, with SSE streaming. (#3, #6, #7)
- Interop is tested in CI against the official Python `a2a-sdk`, in both directions. The official A2A conformance suites
  (a2a-tck and ACTS) run in CI too. Known failures are listed in `conformance/expected-failures.txt`. (#17)
- `DeleteTaskPushNotificationConfig` is idempotent: deleting a config that is gone succeeds (spec 3.1.10). (#17)
- A message part whose `mediaType` the card does not accept is refused with `ContentTypeNotSupported`. `Part.FromData` uses
  `application/json`, so a card that lists only `text/plain` now refuses data parts built with it. (#17)
- An artifact is sealed with `lastChunk: true`. A later `append: true` is refused, also after a restart. `append: false`
  replaces the artifact. (#32)
- Optional artifact integrity hash: `TaskUpdater.AddHashedArtifactAsync` on the producer and `ArtifactVerifier` on the client.
  It detects corruption, not forgery. Experimental. (#32, #35)
- Accepts `history_length` as well as `historyLength`, as ProtoJSON requires. (#8)
- REST stream errors are sent as SSE frames named `error`. All clients skip stream event types they do not know instead of
  ending the stream. (#8)
- A2A 1.1 (release candidate) data model, experimental (`A2AEXP005`): `docs/a2a.proto` is the `v1.1.0-rc` proto, and the
  model has `Task.generation` and `timeline`, `TimelineEntry`, artifact `startGeneration`, `endGeneration` and `role`,
  `generation` on status and artifact events, the `messageUpdate` and `error` stream events, `StreamRequest`, the new request
  fields, `AgentCapabilities.userArtifacts`, the push config `protocolVersion`, and the error `TaskGenerationMismatch`
  (-32010, HTTP 409, gRPC `ABORTED`). 64-bit values are written as ProtoJSON strings and read as strings or numbers.
- A2A 1.1 on the server, opt-in with `A2AServerOptions.EnableProtocolV11()` (experimental): a caller that sends
  `A2A-Version: 1.1` gets the task `generation` (one per change; it equals the event sequence), the `timeline` (client
  messages and agent status entries), artifact `startGeneration` / `endGeneration`, `generation` on every stream event, a
  `messageUpdate` event for each follow-up message, and send streams that stay open on `INPUT_REQUIRED` and
  `AUTH_REQUIRED` until the task ends. `timelineLength` works like `historyLength` (cap: `A2ALimits.MaxTimelineLengthReturned`,
  100). A card completed from the mapped bindings lists the 1.1 interfaces first. 1.0 and v0.3 callers get the 1.0 shape
  and no message events, and webhooks stay 1.0.
- A2A 1.1 for 1.1 callers: `configuration.ifGenerationMatch` refuses a follow-up when the task's generation differs
  (`TaskGenerationMismatch`, -32010, HTTP 409, gRPC `ABORTED`, with `expectedGeneration` and `currentGeneration`); GetTask
  with `currentGeneration` (REST `?currentGeneration=`) waits until the task changes, up to
  `A2AServerOptions.LongPollTimeout` (30 s, at most 5 min), holding a stream slot; `SubscribeToTask` with
  `lastSeenGeneration` (REST `?lastSeenGeneration=`) resumes after it without a snapshot; a push config created by a 1.1
  caller has `protocolVersion` 1.1 (or the `1.0` it asks for), and its webhook gets `A2A-Version: 1.1`, the `generation` of
  each event and the message updates. Configs of 1.0 callers stay 1.0.
- A2A 1.1 client-written artifacts (`AgentCapabilities.UserArtifacts`): a 1.1 caller may send `artifactUpdates` with its
  message, or without a message to an existing task. They are stored after the message, in list order, each one change,
  with `role` user; the client's `generation` is ignored. The agent reads them in `AgentRequestContext.UserArtifacts`. The
  agent cannot change a client's artifact and the client cannot change an agent's. At most
  `A2ALimits.MaxUserArtifactsPerRequest` (16) per request, each held to the message limits and input media types. Without the
  capability the request is `UnsupportedOperation`. `SendMessageRequest.Message` is now nullable for these requests.
- A2A 1.1 live exchange (`SendLiveMessage`, spec §3.1.12), experimental: gRPC bidi `SendLiveMessage`; JSON-RPC
  `SendLiveMessage` and REST `POST /message:live`, where the `Accept: text/event-stream` call is the stream and other calls
  send input to its task. The first request binds the exchange to a task (message or `subscribe`); a refused request is a
  `StreamError` event and the exchange stays open. Client: `A2AClient.OpenLiveAsync` returns an `A2ALiveConnection` on all
  three bindings. An agent that reads `AgentRequestContext.LiveInput` takes messages while it runs, in any non-terminal
  state (`A2ALimits.LiveInputCapacity`, 32). The client now reads `messageUpdate` and `error` stream events on HTTP.
- A follow-up message is now stored as an event of the task (a message update), so it takes a sequence number. 1.0
  callers do not see it, but stream ids and webhook sequences skip its number.
- The Agent Card endpoint answers with `application/a2a-agent-card+json` when the request asks for it, and with
  `application/json` otherwise. The client asks for both.
- A completed card lists a gRPC interface over TLS as `host:port` (for example `agent.example.com:443`), the form the spec
  shows and other SDKs pass to a gRPC channel. A plain-text gRPC interface keeps its `http://` URL.

### Security

- Tasks, contexts, push configs, idempotency keys and event logs belong to a tenant and owner. Another caller's data looks
  like missing data (`TaskNotFound`). (#5, #7)
- An authorization hook runs on every binding. The app will not start when A2A endpoints have no authorization policy,
  unless anonymous access is turned on explicitly. (#5, #7)
- Default limits on request size, message parts, JSON depth, page size, event size and open streams. Rate limiting per
  caller, and per remote IP address for anonymous callers. (#5)
- SSRF guard for outbound HTTP, including webhooks, `jku` key sets and URLs chosen by a peer (`SafeHttp`). (#5, #8, #13)
- HTTP transports refuse a response that arrived through a redirect. (#8)
- Agent Card signing and verification (JWS, JCS).
- Peer identity is the card origin plus the signing key id, never the card's `name`. `AgentRegistry` rejects duplicate names
  and warns about lookalike names. A cached card is checked again against the current trust policy. (#23)
- Credential providers are chosen by the card's security schemes: OAuth2 client credentials, API key and mutual TLS. A credential
  goes only to the origin the card came from and is never sent across a redirect. (#24)
- `OAuthTokenExchanger` and `UseBearerToken` follow the same redirect and origin rules. `OAuthTokenExchanger` no longer takes an
  `HttpClient`, and `UseBearerToken` now takes the agent URL. (#26)

### Delegation

- `metadata["actorChain"]` records who the work is for (shape of A2A#2028). It is audit data, not authorization. A well-formed
  chain is copied to the task. Experimental. (#22, #35)
- `A2ALimits.MaxActorChainHops` (default 8) refuses a longer chain with `InvalidRequest`. It is a loop brake, not a security
  boundary. (#22)
- Token exchange (`ITokenExchanger`, `OAuthTokenExchanger`, RFC 8693 style). Agent code never gets the caller's token. (#22)

- Audit trail: an `A2AAuditSink` receives one event for each task start, follow-up, approval, answered form, cancel, artifact download,
  push config change, access denial and admin delete, redrive or dead-letter listing, on every binding. Events hold ids and
  reasons, never content. The default sink logs; `AddPostgresAuditSink` stores them in `a2a_audit_log`. Fail open by default;
  `Audit.FailClosed` writes an `Attempted` row first and refuses the request when that write fails. The admin classes have
  overloads that take an `actor`. See the audit guide.

### Skill selection

- A client names the skill it wants with `A2AClient.SelectSkill`. The agent reads `AgentRequestContext.RequestedSkillId`. It is
  an optional extension, and a hint, not authorization. A card can opt in to refusing unknown skills. Experimental. (#30, #35)

### Typed skills

- `A2A.Server.TypedSkills` (`AddTypedSkills`): declare a skill with .NET input and output types from a source-generated JSON
  context. The SDK puts JSON Schema on the card, refuses bad input with `InvalidParams` (a `google.rpc.BadRequest` detail)
  before the agent runs and before anything is stored, and sends the result as one data artifact. A plain executor can run
  next to it as the fallback. AOT safe. Experimental.
- `A2AClient.CallSkillAsync` calls a typed skill with .NET types. `TypedSkillCallException` reports a task that did not
  complete. `TypedSkillExtension.TryGetSchemas` reads the schemas from a card. Experimental.

### Skill authorization

- A host can allow a caller one skill and deny another. `A2AAuthorizationHandler.AuthorizeSkillAsync` decides (it denies by
  default), and `A2AServerOptions.SkillAuthorization` picks the mode: `Off`, `WhenNamed` or `Required`. The server checks the
  skill in `metadata["skillId"]` before the agent runs, on every binding, and an unknown skill gives the same error as a denied
  one. Open streams are checked again. `FilterExtendedCard` lists only allowed skills on the extended card.
  `AgentRequestContext.AuthorizedSkillId` and `IsSkillAllowedAsync` let an agent that picks its own skill ask the same question.

### Microsoft.Extensions.AI

- `A2A.Extensions.AI` runs any `IChatClient` as an agent (`AddChatClientAgent`), calls a remote agent as a tool
  (`AsAIFunction`) and as a chat client (`AsChatClient`). URL parts are refused unless allowed. (#21)

### MCP bridge

- `A2A.Mcp` serves the skills of a remote agent as MCP tools (`AsMcpTools`), with `get_task` and `cancel_task` for long tasks.
  It also runs MCP tools as an agent (`AddMcpToolAgent`) with an allowlist. Credentials are never forwarded, and tool
  descriptions are fixed text unless you opt in. Built on `ModelContextProtocol.Core` 2.4.0.

### v0.3 compatibility

- Opt-in support for A2A v0.3 peers over JSON-RPC and HTTP+JSON. Server: `EnableV03Compat()` in `MapA2A`. Client:
  `V03CompatClientFactory`. v0.3 push and gRPC are not supported. (#25)

### Streaming

- Every task has an event log with a per-task sequence. Events reach subscribers in order, with no repeats. (#5)
- Clients resume a stream with `Last-Event-ID`. (#5)
- A stream that falls behind fills the gap from the event log. If the log no longer has those events, the stream sends a
  snapshot of the task instead. Set `StreamingOptions.ReplayBufferSize` above the number of events your tasks produce if
  clients must see every event. (#5, #13)
- Limits on open streams per task and per caller, on stream duration, and a periodic re-authorization of open streams. (#5)

### Task safety

- `A2AServerOptions.MaxTaskDuration` fails a task that runs too long. `MaxTurnsPerTask` caps the turns in a multi-turn task.
  Both are off by default. (#9)
- `A2AClientOptions.OnMessageReceived` lets a client check every agent message, for example against the user's intent. (#9)
- Single-use approval binding (`PendingApproval`). A follow-up with a `messageId` already in the task's history is not run
  again. (#12)
- `A2ALimits.MaxTaskBytes` (default 64 MiB) and `A2ALimits.MaxArtifactsPerTask` (default 1024) cap what a stored task can hold.
  An agent event past a cap fails the task and stops the agent. A follow-up past the byte cap is refused with
  `UnsupportedOperation` and the task stays as it was.
- A follow-up that was accepted but could not start fails its task, so the task does not wait forever. (#15)
- The client refuses a request with a missing required field before sending it, on every operation and binding, and names the
  field in the error (`InvalidParams`). No member became C# `required`.
  v0.3 clients must give a config id to get a push config; use the list operation otherwise. (#27)

### Structured elicitation

- Optional extension that lets an agent in `INPUT_REQUIRED` ask for a small typed form. `TaskUpdater.RequireInputAsync(context, form, text)` on
  the server, `UseElicitation`, `GetElicitationForm` and `AnswerElicitationAsync` on the client. All bindings. A client that does not
  activate it sees the text only.
- The server checks an answer against the form before the agent runs. A refused answer is `InvalidParams`, is not stored and uses no turn.
  Fields that look like requests for secrets are refused by default.

### Durable stores and running several instances

- `A2A.Server.Postgres` stores tasks, event logs, push configs, the push outbox and idempotency keys. Npgsql, no EF. (#10, #12)
- `A2A.Server.Redis` fans live events out to every instance and provides an execution coordinator, so CancelTask and
  blocking waits work across instances. (#10, #12)
- A task update, its event and its webhooks are saved together or not at all (`TaskStore.CommitAsync`). (#12)
- Postgres deadlines use the database clock, so instances whose clocks disagree still agree on what has expired. (#18)
- An execution that loses its lease before its first event is no longer cancelled by mistake. (#18)

### Push notifications

- Durable delivery through an outbox, with retries, backoff, dead letters and redrive (`A2APushAdmin`). (#12)
- Standard Webhooks signatures (`webhook-signature`) with replay protection, and `PushNotificationReceiver.VerifySignature`
  for receivers. (#12)
- Webhook requests send `A2A-Version`. (#8)
- A webhook registered late is not lost. `CreateTaskPushNotificationConfig` on a task that is terminal, `INPUT_REQUIRED` or
  `AUTH_REQUIRED` queues one delivery of the task snapshot (a `StreamResponse` with `task`) for that config, at the task's
  latest event sequence. The engine also lists a task's webhooks again after an event that leaves it final-for-now, so a
  config saved between its first listing and the commit gets that event too. No locks, so it works across instances. Both
  paths can queue the same config; the outbox keeps one (new `PushOutbox.EnqueueAsync`, idempotent on task, config and
  sequence, in the in-memory and PostgreSQL outboxes), and the delivery id is derived from that key.
  `PushNotifications.CatchUpOnRegistration` (default true) turns both off. Inline config on `SendMessage` is still the
  race-free way to register for a new task.
- `PushDelivery.MaxDeliveryAge` (default 24 hours): a delivery older than this, counted from when it was queued, is
  dead-lettered. PostgreSQL uses the database clock; the new nullable `a2a_push_outbox.queued_ticks` column is added by the
  schema script and is safe on existing rows. `PushDelivery.MaxAttempts` is now `int?` and defaults to null (no count
  limit; it caps attempts when set). Before, the default was 10 attempts.
- `PushDelivery.OnDeadLetter`: an optional callback with the task, config id, sequence and last error (never secrets),
  called when a delivery is dead-lettered. An exception it throws is logged and has no other effect.
- Custom `PushOutbox` classes must implement the new abstract `EnqueueAsync`.
- `PushNotificationReceiver.HandleAsync` checks the token and/or signature, runs your handler, and counts the delivery id as done
  only after the handler succeeds. It returns the status to answer: 200 (handled or duplicate), 503 (same id in progress,
  retryable), 500 (handler failed, the retry reaches the handler again), 401 (bad token or signature), 400 (bad body).
  `Validate` and `VerifySignature` mark the id as seen at once, so a failure after them lost the retry; their docs now say so.
- `IPushDeliveryStore` (default `InMemoryPushDeliveryStore`, 10-minute window) keeps handled ids, so they can survive a restart.
  With the in-memory store a restart can repeat a delivery, which is safe.
- `PushTaskTracker` notices lost webhooks: it calls `GetTask` when a task's deadline passes without a final state, or when
  `X-A2A-Notification-Sequence` skips a number, and reports the result to a callback. `IPushTaskStore` (default in memory), `TimeProvider` aware.
- `A2AClient.RegisterPushAsync` registers the webhook first, then calls `GetTask` once, and returns both, so a caller sees a task that
  already finished (servers of other SDKs send no catch-up delivery).

### Proactive continuation

- `TaskExecutionEngine.ContinueTaskAsync` continues an existing task with no inbound request, for an agent that implements
  `IContinuableAgentExecutor`. It needs no push registration. Events go to the event store, streams and push.
- The run is exclusive against every execution of the task on every instance. A request that cannot start returns a status
  (`TaskNotFound`, `TaskTerminal`, `InterruptedTimedOut`, `AlreadyRunning`, `NotSupported`) and changes nothing.
- The API does not check the caller. A host that exposes it must authenticate its callers.

### Client outbox

- A durable list of the messages an agent sends to peers: `A2A.Client.Outbox` (`ClientOutboxStore`, `InMemoryClientOutboxStore`, `ClientOutboxDispatcher`) and the new package
  `A2A.Client.Postgres`, which can enqueue and acknowledge in your own database transaction. Sends carry the entry id as `Idempotency-Key`; a crash
  between "call the peer" and "save" cannot create a second task on an SDK server. See "Client outbox" in the guides.

### Retention

- `AddA2ARetention` deletes old tasks and contexts, times out tasks stuck waiting for input (24 hours by default), and fails
  tasks whose instance died. It is opt-in. (#12)
- A sweep step no longer stalls behind items it has to skip. (#18)
- With an artifact object store, the sweep also deletes objects that no task refers to, and `A2ATaskAdmin.DeleteTaskAsync`
  deletes a task's objects after its row. See "Artifact storage".

### Artifact storage

- Large artifact bytes can live in an object store instead of the task row. Register an `ArtifactObjectStore` (abstract class, in
  `A2A.Server.Storage`) and set `A2AServerOptions.ArtifactStorage.PublicBaseUrl`. `InMemoryArtifactObjectStore` is for tests and
  development. With no store registered, nothing changes except the size caps.
- New packages `A2A.Server.Storage.S3` (`AddA2AS3ArtifactStore`; Amazon S3 and S3-compatible services) and
  `A2A.Server.Storage.AzureBlob` (`AddA2AAzureBlobArtifactStore`; block blobs). Uploads go in 8 MiB steps and a failed upload leaves
  no object. Download links force `Content-Disposition: attachment`. S3 needs the tagging permissions
  (`s3:PutObjectTagging`, `s3:GetObjectTagging`); Azure makes SAS links only from an account key.
- A raw part over `InlineThresholdBytes` (256 KiB) is uploaded and replaced by a `url` part with `sha256` and `sizeBytes` in its
  metadata. `TaskUpdater.AddArtifactFromStreamAsync` uploads a stream of up to `MaxObjectBytes` (5 GiB) without passing it
  through an event. Appended chunks stay inline.
- `MapA2AArtifacts()` serves `GET {pattern}/artifacts/{tenant}/{taskId}/{objectId}`. The caller needs the same credentials as
  for A2A calls, and the read is authorized as `GetTask`. Every miss is 404, also an object that is missing in `Redirect` mode.
  There are no signed links.
- `A2AArtifactDownloader` (.NET 8 and later) downloads a `url` part. It takes the caller's `HttpMessageHandler`, which must
  not follow redirects, and refuses a URL on another origin. It follows one redirect to a presigned URL without credentials,
  and checks `sizeBytes` and `sha256`.
- `TaskStore` has a new abstract member, `GetForMaintenanceAsync`, which reads a task without its owner. Custom task stores
  must implement it.
- Startup fails when a store has no link base, and warns (event 255) when a durable task store uses the in-memory object store.
  Counters: `a2a.server.artifact.objects.written`, `.bytes.written`, `.bytes.served` and `.orphans.deleted`.

### Hosting several agents

- One host can serve many agents, one per tenant: `AddTenantCardProvider` and `AddAgentExecutorResolver`. (#12)
- The Agent Card signature cache now evicts the least recently used card. (#18)

### Observability

- Spans for every server and client operation, agent run and webhook delivery through `ActivitySource`, plus metrics
  through `Meter`. No OpenTelemetry package is needed in the core packages. (#11, #13)
- All bindings send and read W3C `traceparent` and `tracestate`. (#11)
- Tags never contain message content, tokens, credentials, webhook URLs or exception messages. (#11)
- Span and attribute names follow the OpenTelemetry A2A semantic conventions (status: Development). Span names lose the `a2a.`
  prefix, `rpc.method` is now `a2a.method.name`, and `a2a.context.id` is now `gen_ai.conversation.id`. (#29)

### Extending the SDK

- `IA2ARequestHandler`, used by every binding, and an operation filter pipeline (`IA2AOperationFilter`). (#13)
- Extension operations over JSON-RPC, REST and gRPC (`AddExtensionOperation`), denied unless authorized. gRPC serves them through
  `a2a.extensions.v1.A2AExtensionService`, mapped with `MapA2AGrpcExtensions`. Experimental. (#13, #35)
- Custom operations are finished: a call must activate the extension of its operation (`A2A-Extensions`), or it fails with
  `ExtensionSupportRequired`; the tenant's card is checked first; `rpc.` names are refused (`A2AProtocol.Methods.IsReserved`);
  a typed `AddExtensionOperation<TRequest, TResult>`; client helpers (`InvokeExtensionOperationAsync`, on JSON-RPC, HTTP+JSON
  and gRPC); and a guide.
- gRPC errors list the activated extensions in the trailers, as the other bindings do. (#35)
- `IResumableTransport` and `StreamFrame`, so third-party transports get stream resume. (#13)
- `IAgentCardProvider` and `IPushConfigChangeListener`. (#13)
- The server core runs without a DI container. `A2A.Client` depends only on `Microsoft.Extensions.Logging.Abstractions`. (#13)

### Ease of use

- `services.AddA2A(...)` and `app.MapA2A(...)` set up a server in a few lines. `MapA2A` also maps the Agent Card. (#13)
- An Agent Card needs only `Name`, `Description` and `Version`. Its interfaces are filled in from the mapped bindings. (#13)
- `AddA2AClient` limits the response buffer to 16 MiB by default. (#15)

### Versioning

- `[Experimental]` marks APIs tied to unsettled contracts, each with a diagnostic ID: `A2AEXP001` skill selection, `A2AEXP002`
  actor chain, `A2AEXP003` artifact integrity hash, `A2AEXP004` gRPC extension service. They can change in any minor version.
  See the versioning guide. (#35)

### Performance

Short benchmark job, macOS arm64, before and after the design work in #13:

| Benchmark | Before | After |
|---|---|---|
| 1,000-event stream, allocations | 432.6 MB | 20.5 MB |
| 1,000-event stream, time | 407 ms | 9.3 ms |
| SendMessage round trip, allocations | 58.3 KB | 36.1 KB |
| JSON-RPC envelope encode | 1,923 ns, 1,640 B | 114 ns, 480 B |

Each event is encoded once, and SSE, push and replay all write the stored bytes. CI fails on an allocation regression above
10%. (#13, #15, #16)

### Documentation

- A docs site in `docs/site`, built with DocFX: task-oriented guides and a generated API reference. CI builds it, compiles
  every code sample, and checks type names and links. It is not hosted yet. (#19)
- Guides for v0.3 compatibility, Microsoft.Extensions.AI, client credentials, peer identity, skill selection and versioning.
  (#31, #35)

### Before you deploy

These matter on a first deployment. See "Durable stores and several instances" in the README.

- The default stores are in memory, for one instance, and are lost on restart.
- Retention is off unless you call `AddA2ARetention`. Without it, a durable task store keeps finished tasks forever and tasks
  of dead instances stay `WORKING`. Startup logs a warning for this.
- With a durable push outbox, push secrets need `AddDataProtection()` with a persisted, shared key ring and
  `AddA2ADataProtectionPushSecrets()`. Otherwise startup logs an error, and webhooks with secrets fail after a restart.
- With a durable task store and several instances, use the Redis execution coordinator (`AddRedisEventBus` registers it).
  Startup logs a warning if the coordinator is in memory.
- The Postgres stores create their tables at startup. If you set `CreateSchemaOnStartup = false`, apply
  `PostgresSchema.ScriptFor("<schema>")` before you deploy.
- To verify webhooks, register push configs with the scheme `webhook-signature` and call
  `PushNotificationReceiver.VerifySignature`.
