# Limits and rate limiting

The server refuses oversized requests and too many requests by default. You do not have to turn anything on. This guide lists every limit, what a caller sees when it hits one, and how to change it. For per-task time and turn limits, see [task safety](task-safety.md).

## Change the limits

[!code-csharp[](../../../samples/Snippets/LimitsAndRateLimiting.cs#Limits)]

All size, count and time limits are members of <xref:A2A.Server.A2ALimits>, reached as <xref:A2A.Server.A2AServerOptions.Limits>.

| Option | Default | When it is hit |
|---|---|---|
| <xref:A2A.Server.A2ALimits.MaxRequestBodyBytes> | 10 MB | HTTP 413, reason `PAYLOAD_TOO_LARGE` |
| <xref:A2A.Server.A2ALimits.MaxPartsPerMessage> | 100 | `InvalidRequest` |
| <xref:A2A.Server.A2ALimits.MaxRawPartBytes> | 8 MB | HTTP 413, reason `PAYLOAD_TOO_LARGE` |
| <xref:A2A.Server.A2ALimits.MaxJsonDepth> | 32 | `InvalidParams` |
| <xref:A2A.Server.A2ALimits.MaxHistoryLengthReturned> | 100 | No error. The newest messages are returned. |
| <xref:A2A.Server.A2ALimits.MaxListPageSize> | 100 | No error. The page size is capped. |
| <xref:A2A.Server.A2ALimits.MaxConcurrentStreamsPerTask> | 10 | `RATE_LIMITED` (HTTP 429) |
| <xref:A2A.Server.A2ALimits.MaxConcurrentStreamsPerPrincipal> | 50 | `RATE_LIMITED` (HTTP 429) |
| <xref:A2A.Server.A2ALimits.MaxConcurrentExecutions> | 1024 | Waits in a queue, then `RATE_LIMITED` (HTTP 429). See [execution limits](execution-limits.md). |
| <xref:A2A.Server.A2ALimits.MaxConcurrentExecutionsPerTenant> | 0 (off) | As above, per tenant. |
| <xref:A2A.Server.A2ALimits.MaxEventBytes> | 4 MB | The task fails and the agent is stopped. |
| <xref:A2A.Server.A2ALimits.MaxTaskBytes> | 64 MiB | An agent event past it fails the task and stops the agent. A follow-up past it is refused with `UnsupportedOperation`. |
| <xref:A2A.Server.A2ALimits.MaxArtifactsPerTask> | 1024 | The task fails and the agent is stopped. |
| <xref:A2A.Server.A2ALimits.MaxStreamDuration> | 1 hour | The stream ends normally. Clients resume with `Last-Event-ID`. |
| <xref:A2A.Server.A2ALimits.StreamReauthInterval> | 5 minutes | The authorization handler is asked again. On denial the stream ends with `TaskNotFound` (task-scoped streams) or `PermissionDenied`, as a new request would. |
| <xref:A2A.Server.A2ALimits.StreamIdleTimeout> | off | A stream that sent no event for this long ends normally. Clients resume with `Last-Event-ID`. Keep-alive comments are not events. |
| <xref:A2A.Server.A2ALimits.MaxPushConfigsPerTask> | 10 | `InvalidRequest` |
| <xref:A2A.Server.A2ALimits.MaxActorChainHops> | 8 | `InvalidRequest`. See [delegation](auth-and-ownership.md#delegation). |

Notes:

- `MaxRequestBodyBytes`: a larger `Content-Length` is refused before the body is read. Without one, reading stops once the limit is passed.
- `MaxJsonDepth` applies to request bodies only. Values above 64 have no effect.
- `MaxEventBytes` is measured on the serialized JSON. A direct reply that is too large is not sent. The request gets a `FAILED` task instead.
- `MaxTaskBytes` counts the content of the parts in the history and in the artifacts: text as UTF-8 bytes, `raw` as base64 length, a URL as its length and data as its JSON text length. Metadata (key names and JSON values), artifact names and descriptions, and file names are counted too. Ids and media types are not. The few markers the server sets itself are not counted where it sets them. The final `FAILED` event is always stored, so a task at the cap can still end.
- Zero or `Timeout.InfiniteTimeSpan` turns off `MaxStreamDuration`, `StreamReauthInterval` and `StreamIdleTimeout`.
- The history cap also applies when the caller asks for more, or sends no `historyLength`.

## What the caller sees

| Breach | HTTP+JSON | JSON-RPC |
|---|---|---|
| Body or raw part too large | status 413 | status 413, error code `-32600` |
| Rate limit or stream budget | status 429 with `Retry-After` | status 429 with `Retry-After`, error code `-32000` |
| Parts, depth, push configs | status 400 | status 200, error in the body |

The 429 body has the error `RATE_LIMITED`. `Retry-After` is in whole seconds (at least 1). The error also has `metadata.retryable = "true"` and `metadata.retryAfter`. The A2A client retries unary calls after these (see `MaxRetries` on <xref:A2A.Client.A2AClientOptions>). It does not retry streams.

## Rate limit

The server adds a token bucket per caller. Change it with <xref:A2A.Server.AspNetCore.A2ARateLimitingExtensions.AddA2ARateLimiting*>:

[!code-csharp[](../../../samples/Snippets/LimitsAndRateLimiting.cs#RateLimit)]

| Option | Default |
|---|---|
| <xref:A2A.Server.AspNetCore.A2ARateLimitOptions.TokenLimit> (burst) | 20 |
| <xref:A2A.Server.AspNetCore.A2ARateLimitOptions.TokensPerPeriod> | 60 |
| <xref:A2A.Server.AspNetCore.A2ARateLimitOptions.ReplenishmentPeriod> | 1 minute |
| <xref:A2A.Server.AspNetCore.A2ARateLimitOptions.Enabled> | true |

Details:

- `MapA2A` applies the policy named `a2a`. The policy has no queue: a request without a token is rejected at once.
- Each tenant and caller pair has its own bucket. Every request also draws from a bucket per caller across all tenants, with the same settings, so spreading requests over tenants gives no extra budget. An anonymous caller has no tenant in its key: the limit runs before authorization and the tenant is text from the URL, so random tenant names would otherwise create buckets without end. Anonymous callers from one address share one bucket across all tenants.
- To use your own policy, register one named `a2a` with `AddRateLimiter`. The default is then skipped.
- The rejection is written in the format of the binding (JSON-RPC or HTTP+JSON). A binding package such as gRPC registers an <xref:A2A.Server.AspNetCore.IA2ARejectionWriter> for its own format, or puts one in the metadata of its endpoints (`.WithMetadata(writer)`), which is asked first. The v0.3 HTTP+JSON routes of `EnableV03Compat` answer in the v0.3 error format this way. v0.3 JSON-RPC calls share the v1.0 JSON-RPC endpoint, so they get a v1.0 JSON-RPC error.

### UseA2A is required

Add the middleware with `app.UseA2A()`. It calls `UseRateLimiter()`, so do not call that yourself. Put it after `UseAuthentication()` and before `UseAuthorization()`. If A2A endpoints use the rate limit and `UseA2A()` was not called, the app does not start.

To turn rate limiting off, set `Enabled = false`. `MapA2A` then adds no policy and `UseA2A()` is not required:

[!code-csharp[](../../../samples/Snippets/LimitsAndRateLimiting.cs#RateLimitOff)]

## Who counts as a caller

<xref:A2A.Server.ServerCallContext.LimitPartition> is the key for the rate limit and for the open-stream budget:

- Authenticated caller: `user:{principal}`.
- Anonymous caller: `anon:{ip}`, so anonymous callers do not share one budget.
- An IPv6 anonymous caller is counted by its /64 prefix, for example `anon:2001:db8:1:2::/64`. One host usually controls a whole /64 and could otherwise use a new address for every request.
- An IPv4-mapped IPv6 address counts as the IPv4 address. No address at all gives `anon:unknown`.

### Behind a proxy

Behind a reverse proxy or load balancer, every connection comes from the proxy. All anonymous callers then share the proxy's budget. Enable the forwarded headers middleware and list the proxy:

[!code-csharp[](../../../samples/Snippets/LimitsAndRateLimiting.cs#Proxy)]

## Open streams

`MaxConcurrentStreamsPerTask` and `MaxConcurrentStreamsPerPrincipal` count open `SendStreamingMessage` and `SubscribeToTask` streams. The per-task count is kept per owner, so another caller's streams never affect your task. The per-caller count uses the tenant and `LimitPartition`. A stream over budget is refused with `RATE_LIMITED` and `Retry-After: 1`. The counts live in memory, per server instance.

## What can go wrong

- **The app does not start and asks for `app.UseA2A()`.** Add it, or set `Enabled = false`.
- **Anonymous users get 429 together.** The proxy address is the partition. Enable forwarded headers.
- **Large uploads get 413.** Send big content by URL, or raise the limits.
- **A long stream ends by itself.** That is `MaxStreamDuration`. Resume with `Last-Event-ID`; see [streaming](streaming.md).

## See also

- [Execution limits and budgets](execution-limits.md)
- [Task safety](task-safety.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Streaming and resume](streaming.md)
