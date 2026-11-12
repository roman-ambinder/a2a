# Push notifications

Push notifications let an agent call a client's webhook (an HTTPS endpoint the client hosts) when a task changes, so the client does not have to keep a stream open or poll. Use them for long tasks. This guide covers the server side (delivery, retries, the SSRF guard, secrets) and the client side (registering and receiving).

The server writes each delivery to an outbox (a queue of pending deliveries in the store) in the same commit as the event. A hosted service, <xref:A2A.Server.Push.PushDeliveryService>, sends them.

## Turn push on

The agent card must declare the capability. Without it, registering a webhook fails with `PushNotificationNotSupported`, and no deliveries are made.

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#Capability)]

The client then registers a webhook for a task. The secret is a signing secret for [webhook signatures](#webhook-signatures).

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#Register)]

You can also register the webhook in the first message. Leave `TaskId` empty; the server creates the task.

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#RegisterInline)]

The server returns the config with `Token` and `Credentials` blanked. A task can have at most <xref:A2A.Server.A2ALimits.MaxPushConfigsPerTask> configs (default 10). A new config gets deliveries from the next event on. A pending delivery uses the config as it is when it is sent.

### Late registration

A webhook registered with `CreateTaskPushNotificationConfig` after the task stopped moving still hears about it. If the task is terminal, `INPUT_REQUIRED` or `AUTH_REQUIRED` when the config is created, the server queues one delivery for that config only. Its body is a `StreamResponse` with `task`: the task as it is now. `X-A2A-Notification-Sequence` is the task's latest event sequence. For a running task nothing is sent; the config gets the next event as usual.

The server saves the config first, then reads the task. The engine also lists the task's webhooks again after an event that ends or interrupts the task, and queues the same snapshot for any config it had missed. So a registration that lands while that event is committed is not lost, on one instance or several, without locks. When both paths fire for one config, the outbox keeps one delivery (the key is task, config id and sequence), and its delivery id is derived from that key, so a receiver that drops duplicate ids sees it once.

Set `PushNotifications.CatchUpOnRegistration = false` to turn this off (both the snapshot and the second listing). A webhook then only sees later events.

For a new task, put the config inline in `SendMessage`. It is saved before the task exists, so it sees every event, with no race at all.

## How delivery works

- Each event creates one delivery per push config. The body is the event as committed (a `StreamResponse`), so delivery does not depend on event log retention.
- Delivery is at least once. Make your receiver idempotent. A delivery that was acknowledged and then lost cannot be detected by the server: see [Detect lost webhooks](#detect-lost-webhooks).
- Order holds per task and per config. A delivery waits until the one before it was sent or dead-lettered.
- A delivery is leased while it is sent. If an instance dies, the lease ends and another instance sends it. Several instances can share one outbox.
- If the config was deleted, the delivery is dropped.
- The default outbox is in memory. For a durable one, use a durable store; see [Durable stores](durable-stores.md).

### Retries

| Result | What happens |
|---|---|
| 2xx | Sent. |
| 408, 429, 5xx, timeout, connection error | Retried with backoff. |
| Any other status (other 4xx, and 3xx, because redirects are not followed) | Dead-lettered. |
| URL refused by the SSRF guard, unusable signing secret, secrets that cannot be unprotected | Dead-lettered. |
| `MaxAttempts` is set and the last attempt fails | Dead-lettered (moved to a dead-letter list and not sent again; see [Dead letters](#dead-letters)). |
| The delivery is older than `MaxDeliveryAge` | Dead-lettered, without another attempt. |

The wait before retry *n* is `InitialBackoff × 2^(n-1)`, with ±20 % jitter, capped at `MaxBackoff`.

### Retry policy

To decide the wait yourself, set `PushDelivery.RetryPolicy`. It is called after each failure in the "Retried with backoff" row above, and gets a <xref:A2A.Server.Push.PushRetryContext>: the task, config id, sequence, the attempt that failed (the first is 1), the error, the HTTP status (null for a timeout or a connection error), the receiver's `Retry-After` as a wait (null when it sent none), and `DefaultDelay`, the wait the default backoff would use. It never gets secrets.

Return the wait before the next attempt, or null to dead-letter the delivery now. A negative wait counts as zero, and a wait longer than 30 days counts as 30 days. If the policy throws, the exception is logged (event 316) and the default backoff is used.

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#RetryPolicy)]

`MaxAttempts` and `MaxDeliveryAge` still apply: the policy is not asked after the last allowed attempt, and a delivery older than `MaxDeliveryAge` is dead-lettered when it is next leased, whatever the policy said. Failures in the "Dead-lettered" rows are dead-lettered without asking it.

### Options

Set these on <xref:A2A.Server.A2AServerOptions.PushDelivery> (<xref:A2A.Server.Push.PushDeliveryOptions>) and <xref:A2A.Server.A2AServerOptions.PushNotifications> (<xref:A2A.Server.Push.PushNotificationOptions>).

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#DeliveryOptions)]

| Option | Default | Meaning |
|---|---|---|
| `PushDelivery.InitialBackoff` | 1 second | Wait before the first retry. |
| `PushDelivery.MaxBackoff` | 5 minutes | Longest wait between attempts. |
| `PushDelivery.MaxAttempts` | null | Attempts per delivery, the first included. null: no count limit. |
| `PushDelivery.MaxDeliveryAge` | 24 hours | Longest time a delivery may stay queued, from when it was queued. null: no limit. |
| `PushDelivery.OnDeadLetter` | null | Callback when a delivery is dead-lettered. |
| `PushDelivery.RetryPolicy` | null | Picks the wait before a retry, or dead-letters. null: the backoff above. |
| `PushDelivery.Lease` | 1 minute | How long a leased delivery is hidden from other instances. |
| `PushDelivery.PollInterval` | 1 second | How often the outbox is checked for due deliveries. |
| `PushDelivery.BatchSize` | 50 | Most deliveries one instance sends at the same time. |
| `PushDelivery.MaxInFlightPerCaller` | 20 | Most deliveries of one caller (the task owner) in flight at once, so one caller's slow webhooks cannot take every send slot. A soft cap across instances. null: no cap. |
| `PushNotifications.Timeout` | 15 seconds | Timeout of one POST, and of the DNS check before it. |
| `PushNotifications.CatchUpOnRegistration` | true | Send the task snapshot to a webhook registered on a final or interrupted task. |
| `PushNotifications.AllowInsecureUrls` | false | Allow `http://` URLs. |
| `PushNotifications.AllowPrivateNetworks` | false | Turn the private-address block off. |
| `PushNotifications.AllowedHosts` | empty | If not empty, only these hosts are allowed. |
| `PushNotifications.AllowedNetworks` | empty | Networks that bypass the private-range block. |
| `PushNotifications.Proxy` | null | Proxy for webhook calls. With null, the system proxy is ignored. |

The service checks the options at startup. `Timeout` must be positive and finite, `PollInterval` must be above zero, `MaxDeliveryAge` (when set) must be above zero, `MaxAttempts` and `MaxInFlightPerCaller` (when set) must be at least 1, and `Lease` must be longer than `2 × Timeout + 5 s`. Otherwise startup throws `InvalidOperationException`.

### Headers on each POST

| Header | Value |
|---|---|
| `Content-Type` | `application/a2a+json` |
| `A2A-Version` | `1.0`, or `1.1` for a config registered with protocol version 1.1 |
| `X-A2A-Delivery-Id` | The delivery id. The same on every retry. |
| `X-A2A-Notification-Sequence` | The event sequence. An SDK extension, not in the spec. |
| `X-A2A-Notification-Token` | The config `Token`, when set. |
| `Authorization` | `{Scheme} {Credentials}` from the config's authentication. With no authentication, or with the `webhook-signature` scheme, and a `Token`: `Bearer {Token}`. The signing secret is never sent. |
| `webhook-id`, `webhook-timestamp`, `webhook-signature` | Only for the `webhook-signature` scheme. |
| `traceparent`, `tracestate` | The trace of the delivery span, when there is one. |

## Webhook signatures

Signatures follow the [Standard Webhooks](https://www.standardwebhooks.com/) scheme. Register the config with `Authentication.Scheme = "webhook-signature"` and `Credentials = "whsec_" + base64(secret)`. The secret must be at least 16 bytes.

Each POST then has:

- `webhook-id`: the delivery id (same on retries).
- `webhook-timestamp`: Unix seconds. New on every attempt.
- `webhook-signature`: `v1,` plus the base64 HMAC-SHA256 of `{id}.{timestamp}.{body}`.

The secret is never sent as an `Authorization` header.

On the receiver, use <xref:A2A.Client.Push.PushNotificationReceiver>. See [Receiving deliveries](#receiving-deliveries).

<xref:A2A.Client.Push.PushNotificationReceiver.VerifySignature*> returns a <xref:A2A.Client.Push.PushValidationResult>:

| Result | Meaning |
|---|---|
| `Valid` | The signature matches and the timestamp is in range. |
| `InvalidSignature` | A header is missing or malformed, or no `v1` signature matches. |
| `Stale` | The signature is valid, but the timestamp is outside the tolerance (default 5 minutes). |
| `Duplicate` | This `webhook-id` was already accepted. |

A longer `tolerance` keeps ids longer, until the timestamp plus the tolerance, because a replay stays acceptable that long.

If you use the token instead of a signature, `Validate` checks `X-A2A-Notification-Token` or a bearer `Authorization` header in constant time.

> [!WARNING]
> `Validate` and `VerifySignature` mark the delivery id as seen **at once**, before your code handles the event. If your code then fails and you answer 500, the server retries with the same id, the helper says `Duplicate`, and the event is lost. Prefer `HandleAsync`. If you use these two, nothing may fail after they return `Valid`.

## Receiving deliveries

Two rules for a receiver:

1. **Save the event first, then answer 2xx.** The server treats 2xx as "done" and never sends that delivery again.
2. **A webhook is a hint; the task is the truth.** A webhook can be lost after you acknowledged it, and the server cannot see that. Read the task with `GetTask` when it matters (see [the tracker](#detect-lost-webhooks)).

<xref:A2A.Client.Push.PushNotificationReceiver.HandleAsync*> does the checks and calls your handler. The delivery id counts as done only after the handler succeeds. Keep one receiver instance. Pass the raw body bytes, not re-serialized JSON.

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#Receiver)]

You give the token (`expectedToken`), the decoded signing secret (`signingSecret`), or both. The handler gets a <xref:A2A.Client.Push.PushDelivery>: the parsed `StreamResponse`, the delivery id and the sequence. With a signing secret, the delivery id is the signed `webhook-id`, never the unsigned `X-A2A-Delivery-Id`, so a replay cannot get past the duplicate check by changing a header; without one it is `X-A2A-Delivery-Id`. The result says what to answer:

| Outcome | Status | Meaning |
|---|---|---|
| `Handled` | 200 | The handler ran and succeeded. |
| `Duplicate` | 200 | This id was handled before. The handler did not run. Answer 2xx so the sender stops. |
| `InProgress` | 503 | The same id is being handled by another call. The sender retries it later. |
| `HandlerFailed` | 500 | The handler threw. The id is forgotten, so the retry reaches the handler again. |
| `Unauthorized` | 401 | Bad token, bad signature, or stale timestamp. The handler did not run. |
| `BadRequest` | 400 | The credentials are right, but the body is not a `StreamResponse`. |
| `UnexpectedTask` | 404 | `ExpectedTask` is set and the event is for a task you did not create, or has no task id. The handler did not run. |
| `RateLimited` | 429 | `MaxDeliveriesPerMinute` is set and reached. The handler did not run and the id is not remembered. Send `Retry-After` from `RetryAfter`; the sender retries. |

**Check the task id** (spec 4.3.3: clients must check that a notification is for a task they expect). Set <xref:A2A.Client.Push.PushNotificationReceiver.ExpectedTask> to a function that says whether a task id is yours; <xref:A2A.Client.Push.PushTaskTracker.IsTrackedAsync*> is one. A delivery for another task is `UnexpectedTask` before your handler runs. The check comes after the duplicate check, so a retry of a delivery you already handled is still `Duplicate` after the task stops being tracked, and a refused delivery is not remembered. Without `ExpectedTask` there is no check, so check the task id in your handler.

The server retries only 408, 429 and 5xx, and dead-letters other 4xx. That is why a call in progress gets 503 and never 409.

An id claimed by a call that never finished (the process died) is free again after `ProcessingLease` (default 5 minutes).

### Where handled ids are kept

The receiver keeps handled ids for at least 10 minutes. By default they are in memory (<xref:A2A.Client.Push.InMemoryPushDeliveryStore>). To keep them across restarts, or share them between instances, implement <xref:A2A.Client.Push.IPushDeliveryStore> and pass it to the receiver.

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#Stores)]

With the in-memory store, a restart can repeat a delivery that was already handled. This is safe if your handler is idempotent, and it never loses an event.

The low-level calls look like this. Use them only if you can guarantee nothing fails after `Valid`:

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#ReceiverLowLevel)]

## Register a webhook safely

Register first, then read the task once. A task that finished before the webhook existed gets no callback from servers of other SDKs, and nothing tells you. <xref:A2A.Client.A2AClient.RegisterPushAsync*> does both in the right order and returns the config and the task.

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#RegisterSafely)]

If `GetTask` fails after the webhook was registered, the exception is thrown and the webhook stays registered.

A config with neither a token nor authentication gets its own token from <xref:A2A.Client.Push.PushTokens.Create*> (32 random bytes, base64url), so each registration has a unique, single-purpose token (spec 13.2). The returned config carries it for your receiver, even though servers leave tokens out of their answers. Keep it as a secret.

**HTTPS only.** Set <xref:A2A.Client.A2AClientOptions.RequireHttpsWebhooks> in production: the client then refuses a webhook URL that is not `https` (loopback is allowed), in `CreateTaskPushNotificationConfig` and in the push config of a `SendMessage`. It is off by default so plain-HTTP test receivers keep working. The SDK's server refuses plain-HTTP webhooks by default anyway.

**Rotate a token** with <xref:A2A.Client.A2AClient.RotatePushTokenAsync*>. It registers the same URL with a new token first and deletes the old config after, so no notification is lost. Accept both tokens at your receiver until it returns. If the delete fails, both configs exist; delete the old one yourself.

**Limit the rate.** Set <xref:A2A.Client.Push.PushNotificationReceiver.MaxDeliveriesPerMinute> to cap the deliveries `HandleAsync` accepts per minute. It counts only deliveries that passed the token and signature checks, so a caller without credentials cannot use up the budget. Over the cap the answer is `RateLimited` (429).

## Detect lost webhooks

<xref:A2A.Client.Push.PushTaskTracker> watches tasks. Give it a task id and a deadline. If the deadline passes without a final state, it calls `GetTask` and gives the result to your callback. A gap in `X-A2A-Notification-Sequence` for a task triggers the same check at once (servers of other SDKs may not send the header; then only the deadline works).

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#Tracker)]

Report each notification from your handler:

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#TrackerNotify)]

- A final state (terminal, or interrupted such as `InputRequired`) removes the task from the tracker. A check that finds a final task removes it too.
- A check that finds the task still running sets the next deadline to the same timeout from now. A failed `GetTask` is reported with `Error` and tried again after the timeout.
- The tracker needs a timer sweep (default every 5 seconds), so a check can run a little after the deadline. Dispose the tracker to stop it.
- Tracked tasks are in memory by default. Implement <xref:A2A.Client.Push.IPushTaskStore> to keep them across restarts. The tracker takes a `TimeProvider` like the rest of the client.

## The SSRF guard

A webhook URL is chosen by the client, so the server checks it. Without a check, a caller could make the server call internal addresses for it (server-side request forgery, SSRF).

- At registration: the URL must be absolute, use `https` (unless `AllowInsecureUrls`), and carry no user info. If `AllowedHosts` is set, the host must be in it. A private IP literal, `localhost`, or an ambiguous numeric host (such as octal) is refused. The host is then resolved and every address is checked. Any DNS-based failure gives the same error, `Push notification URL is not allowed.`, so a caller cannot probe your DNS.
- Before every attempt: the URL and its addresses are checked again.
- At connect time: the push HTTP client checks the resolved IP of the connection, which also blocks DNS rebinding. This holds for direct connections only (see [Through a proxy](#through-a-proxy)).
- Redirects are never followed.

Blocked by default: loopback, `0.0.0.0/8`, `10/8`, `100.64/10` (CGNAT), `169.254/16` (link-local, cloud metadata), `172.16/12`, `192.168/16`, `192.0.0/24`, `198.18/15`, multicast and reserved ranges, and for IPv6 the unspecified, link-local, site-local, multicast and `fc00::/7` ranges. An IPv4 address inside an IPv6 one (mapped, NAT64, 6to4, IPv4-compatible) is judged as IPv4.

### Through a proxy

With `PushNotificationOptions.Proxy` set, a delivery that the proxy handles connects to the proxy, not to the webhook host. The connect-time check then sees only the proxy's address, and the proxy resolves the webhook host itself. The SDK checks the webhook host only with its own DNS lookup before each attempt. A host can resolve to a public address for that lookup and to a private one (for example `169.254.169.254` or `10.0.0.5`) when the proxy looks it up (DNS rebinding). The SDK cannot see or control the proxy's lookup, so it does not block that.

So the proxy must enforce egress policy: refuse private, loopback, link-local, CGNAT and other internal ranges by the address it connects to, after its own lookup. Do not set `Proxy` to a proxy that can reach internal networks unless it does this. `AllowedHosts` limits which names can be registered, but it does not stop a listed name from resolving to an internal address later. A request the proxy bypasses (`IWebProxy.IsBypassed`) connects directly and gets the full connect-time check.

The SDK does not send the proxy a pre-checked IP address in place of the host name. The `CONNECT` target is the name the proxy filters on, so pinning it to an address would break proxy rules by host name, and the guard handler cannot rewrite the request before the proxy is chosen.

The webhook client is registered under the service key `PushDeliveryService.InvokerServiceKey` (`"A2A.PushNotifications"`). You can replace it with your own keyed `HttpMessageInvoker`. Then the guard is your job.

## Push secrets

`Token` and `Credentials` are encrypted before they reach the store, by an <xref:A2A.Server.Push.IPushSecretProtector>. They are decrypted only at delivery time.

The default protector, `EphemeralPushSecretProtector`, uses a random key per process. After a restart, or on another instance, the secrets cannot be read, and those deliveries are dead-lettered. With a durable outbox and the default protector, startup logs an error.

Use ASP.NET Core Data Protection with a key ring that is saved and shared by every instance:

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#DataProtection)]

<xref:A2A.Server.AspNetCore.A2AServiceCollectionExtensions.AddDataProtectionPushSecrets*> (on the A2A builder) replaces the protector. The Data Protection purpose is `A2A.PushSecrets.v1`. You can also write your own `IPushSecretProtector`.

## Dead letters

A dead letter keeps the payload and the last error (never secrets). <xref:A2A.Server.Push.A2APushAdmin> lists them per tenant and sends one again. It is not exposed over A2A, so protect your own endpoint.

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#DeadLetters)]

### Delivery age

A delivery that keeps failing ends by age. `MaxDeliveryAge` (default 24 hours) counts from the moment the delivery was queued. The check runs when the delivery is next leased: if it is older, it is dead-lettered without another attempt, and the dead letter keeps the last error. With PostgreSQL the age is measured on the database clock, so instances whose clocks differ agree. A redriven dead letter starts again at age 0. `MaxAttempts` is null by default; set it to also cap the number of attempts.

### Dead-letter hook

`PushDelivery.OnDeadLetter` is called after a delivery was dead-lettered, for any reason. It gets a <xref:A2A.Server.Push.PushDeadLetterInfo>: the task, config id, sequence and last error. It never gets secrets. Use it to alert or to redrive. If it throws, the exception is logged and nothing else changes. The send slot is held while it runs, so keep it short.

[!code-csharp[](../../../samples/Snippets/PushNotifications.cs#DeadLetterHook)]

A redriven delivery starts again at attempt 0 with the same delivery id. It is sent after newer events of the same config that were delivered in the meantime, not in its original place. Retention deletes dead letters with their task.

## Metrics

The meter is `A2A.Server`. Add it with `AddMeter("A2A.Server")`.

| Counter | Tag | Meaning |
|---|---|---|
| `a2a.server.push.deliveries` | `a2a.outcome` = `sent`, `retried` or `dead_lettered` | One count per attempt. |
| `a2a.server.push.dead_letters` | none | Deliveries moved to the dead letters. |

A task can also change with no client request. See [Proactive continuation](proactive-continuation.md); its events are delivered like any other.

Each attempt is also a span, `a2a.push.deliver`, a child of the agent run that made the event. See [Observability](observability.md).

## What can go wrong

- Deliveries dead-letter after a restart: the push secret protector is per process. Use Data Protection.
- Registration fails with `Push notification URL is not allowed.`: the host does not resolve, is too slow to resolve, or resolves to a private address. A host outside `AllowedHosts` fails with `Push notification host is not allowed`.
- The receiver sees `Stale`: the receiver clock is off, or the delivery is old. Check the clock before you raise the tolerance.
- The receiver sees `InvalidSignature` for good deliveries: it hashed re-serialized JSON, or used the wrong secret.
- An event is missing after a failure in your code: you used `Validate` or `VerifySignature` and failed after them. Use `HandleAsync`.
- A task finished but no callback came: the webhook was lost, or the task finished before it was registered. Use `RegisterPushAsync` and the tracker.
- No deliveries at all: the card lacks `PushNotifications = true`, or the host does not run hosted services.

## See also

- [Durable stores](durable-stores.md)
- [Retention](retention.md)
- [Observability](observability.md)
- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Agent card signing](agent-card-signing.md)
