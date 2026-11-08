# Push notifications

Push notifications let an agent call a client's webhook when a task changes, so the client does not have to keep a stream open or poll. Use them for long tasks.

The server writes each delivery to an outbox in the same commit as the event. A hosted service, <xref:A2A.Server.Push.PushDeliveryService>, sends them.

## Turn push on

The agent card must declare the capability. Without it, registering a webhook fails with `PushNotificationNotSupported`, and no deliveries are made.

[!code-csharp[](../snippets/PushNotifications.cs#Capability)]

The client then registers a webhook for a task. The secret is a signing secret for [webhook signatures](#webhook-signatures).

[!code-csharp[](../snippets/PushNotifications.cs#Register)]

You can also register the webhook in the first message. Leave `TaskId` empty; the server creates the task.

[!code-csharp[](../snippets/PushNotifications.cs#RegisterInline)]

The server returns the config with `Token` and `Credentials` blanked. A task can have at most <xref:A2A.Server.A2ALimits.MaxPushConfigsPerTask> configs (default 10). A new config gets deliveries from the next event on. A pending delivery uses the config as it is when it is sent.

## How delivery works

- Each event creates one delivery per push config. The body is the event as committed (a `StreamResponse`), so delivery does not depend on event log retention.
- Delivery is at least once. Make your receiver idempotent.
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
| Last attempt fails | Dead-lettered. |

The wait before retry *n* is `InitialBackoff × 2^(n-1)`, with ±20 % jitter, capped at `MaxBackoff`.

### Options

Set these on <xref:A2A.Server.A2AServerOptions.PushDelivery> (<xref:A2A.Server.Push.PushDeliveryOptions>) and <xref:A2A.Server.A2AServerOptions.PushNotifications> (<xref:A2A.Server.Push.PushNotificationOptions>).

[!code-csharp[](../snippets/PushNotifications.cs#DeliveryOptions)]

| Option | Default | Meaning |
|---|---|---|
| `PushDelivery.InitialBackoff` | 1 second | Wait before the first retry. |
| `PushDelivery.MaxBackoff` | 5 minutes | Longest wait between attempts. |
| `PushDelivery.MaxAttempts` | 10 | Attempts per delivery, the first included. |
| `PushDelivery.Lease` | 1 minute | How long a leased delivery is hidden from other instances. |
| `PushDelivery.PollInterval` | 1 second | How often the outbox is checked for due deliveries. |
| `PushDelivery.BatchSize` | 50 | Most deliveries one instance sends at the same time. |
| `PushNotifications.Timeout` | 15 seconds | Timeout of one POST, and of the DNS check before it. |
| `PushNotifications.AllowInsecureUrls` | false | Allow `http://` URLs. |
| `PushNotifications.AllowPrivateNetworks` | false | Turn the private-address block off. |
| `PushNotifications.AllowedHosts` | empty | If not empty, only these hosts are allowed. |
| `PushNotifications.AllowedNetworks` | empty | Networks that bypass the private-range block. |
| `PushNotifications.Proxy` | null | Proxy for webhook calls. With null, the system proxy is ignored. |

The service checks the options at startup. `Timeout` must be positive and finite, `PollInterval` must be above zero, and `Lease` must be longer than `2 × Timeout + 5 s`. Otherwise startup throws `InvalidOperationException`.

### Headers on each POST

| Header | Value |
|---|---|
| `Content-Type` | `application/a2a+json` |
| `A2A-Version` | `1.0` |
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

On the receiver, use <xref:A2A.Client.Push.PushNotificationReceiver>. Keep one instance, because it remembers ids. Pass the raw body bytes, not re-serialized JSON.

[!code-csharp[](../snippets/PushNotifications.cs#Receiver)]

<xref:A2A.Client.Push.PushNotificationReceiver.VerifySignature*> returns a <xref:A2A.Client.Push.PushValidationResult>:

| Result | Meaning |
|---|---|
| `Valid` | The signature matches and the timestamp is in range. |
| `InvalidSignature` | A header is missing or malformed, or no `v1` signature matches. |
| `Stale` | The signature is valid, but the timestamp is outside the tolerance (default 5 minutes). |
| `Duplicate` | This `webhook-id` was already accepted. |

The receiver remembers each valid id for at least 10 minutes. A longer `tolerance` keeps ids longer, until the timestamp plus the tolerance, because a replay stays acceptable that long. The memory is in process, so a receiver on several instances needs its own shared check.

If you use the token instead of a signature, `Validate` checks `X-A2A-Notification-Token` or a bearer `Authorization` header in constant time and drops a repeated `X-A2A-Delivery-Id`.

## The SSRF guard

A webhook URL is chosen by the client, so the server checks it.

- At registration: the URL must be absolute, use `https` (unless `AllowInsecureUrls`), and carry no user info. If `AllowedHosts` is set, the host must be in it. A private IP literal, `localhost`, or an ambiguous numeric host (such as octal) is refused. The host is then resolved and every address is checked. Any DNS-based failure gives the same error, `Push notification URL is not allowed.`, so a caller cannot probe your DNS.
- Before every attempt: the URL and its addresses are checked again.
- At connect time: the push HTTP client checks the resolved IP of the connection, which also blocks DNS rebinding.
- Redirects are never followed.

Blocked by default: loopback, `0.0.0.0/8`, `10/8`, `100.64/10` (CGNAT), `169.254/16` (link-local, cloud metadata), `172.16/12`, `192.168/16`, `192.0.0/24`, `198.18/15`, multicast and reserved ranges, and for IPv6 the unspecified, link-local, site-local, multicast and `fc00::/7` ranges. An IPv4 address inside an IPv6 one (mapped, NAT64, 6to4, IPv4-compatible) is judged as IPv4.

The webhook client is registered under the service key `PushDeliveryService.InvokerServiceKey` (`"A2A.PushNotifications"`). You can replace it with your own keyed `HttpMessageInvoker`. Then the guard is your job.

## Push secrets

`Token` and `Credentials` are encrypted before they reach the store, by an <xref:A2A.Server.Push.IPushSecretProtector>. They are decrypted only at delivery time.

The default protector, `EphemeralPushSecretProtector`, uses a random key per process. After a restart, or on another instance, the secrets cannot be read, and those deliveries are dead-lettered. With a durable outbox and the default protector, startup logs an error.

Use ASP.NET Core Data Protection with a key ring that is saved and shared by every instance:

[!code-csharp[](../snippets/PushNotifications.cs#DataProtection)]

<xref:A2A.Server.AspNetCore.A2AServiceCollectionExtensions.AddA2ADataProtectionPushSecrets*> replaces the protector. The Data Protection purpose is `A2A.PushSecrets.v1`. You can also write your own `IPushSecretProtector`.

## Dead letters

A dead letter keeps the payload and the last error (never secrets). <xref:A2A.Server.Push.A2APushAdmin> lists them per tenant and sends one again. It is not exposed over A2A, so protect your own endpoint.

[!code-csharp[](../snippets/PushNotifications.cs#DeadLetters)]

A redriven delivery starts again at attempt 0 with the same delivery id. It is sent after newer events of the same config that were delivered in the meantime, not in its original place. Retention deletes dead letters with their task.

## Metrics

The meter is `A2A.Server`. Add it with `AddMeter("A2A.Server")`.

| Counter | Tag | Meaning |
|---|---|---|
| `a2a.server.push.deliveries` | `a2a.outcome` = `sent`, `retried` or `dead_lettered` | One count per attempt. |
| `a2a.server.push.dead_letters` | none | Deliveries moved to the dead letters. |

Each attempt is also a span, `a2a.push.deliver`, a child of the agent run that made the event. See [Observability](observability.md).

## What can go wrong

- Deliveries dead-letter after a restart: the push secret protector is per process. Use Data Protection.
- Registration fails with `Push notification URL is not allowed.`: the host does not resolve, is too slow to resolve, or resolves to a private address. A host outside `AllowedHosts` fails with `Push notification host is not allowed`.
- The receiver sees `Stale`: the receiver clock is off, or the delivery is old. Check the clock before you raise the tolerance.
- The receiver sees `InvalidSignature` for good deliveries: it hashed re-serialized JSON, or used the wrong secret.
- No deliveries at all: the card lacks `PushNotifications = true`, or the host does not run hosted services.

## See also

- [Durable stores](durable-stores.md)
- [Retention](retention.md)
- [Observability](observability.md)
- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Agent card signing](agent-card-signing.md)
