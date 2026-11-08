# Agent card signing

A signed agent card lets a client check that the card came from the agent it expects and was not changed. The agent signs the card with a private key. The client trusts a public key that it got another way.

Use it when clients find your agent by URL and you want them to detect a forged or changed card.

## Generate a key

Use an EC P-256 key (algorithm `ES256`). Both of these formats work with the sample:

```bash
# PKCS#8
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out card-signing-key.pem

# or SEC1
openssl ecparam -name prime256v1 -genkey -noout -out card-signing-key.pem
```

[samples/EchoAgent](https://github.com/roman-ambinder/a2a/blob/main/samples/EchoAgent/Program.cs) reads the file named by `EchoAgent:SigningKeyFile` with `ECDsa.ImportFromPem`, and signs with `ES256`, so the key must be P-256. `EchoAgent:SigningKeyId` is the key id (default `echo-1`).

## Sign on the server

Create an <xref:A2A.Signing.AgentCardSigningKey> and set it on <xref:A2A.Server.AspNetCore.A2AHttpOptions.SigningKey>.

[!code-csharp[](../snippets/AgentCardSigning.cs#ServerKey)]

The arguments are the key, a key id (`kid`), the algorithm, and an optional `jku` URL. The `jku` goes into the signature header. Clients use it only if they allow its host (see below).

What happens:

- `GET /.well-known/agent-card.json` returns the card with one signature added. A signature already on the card is kept.
- The public key is served as a key set at <xref:A2A.Server.AspNetCore.A2AHttpOptions.JwksPath> (default `/.well-known/jwks.json`). The set holds only the current key. Without a key, this path is 404.
- Both responses have `Cache-Control: max-age` of <xref:A2A.Server.AspNetCore.A2AHttpOptions.AgentCardMaxAge> (default 5 minutes). The card has an `ETag`. A signed card gets a weak one.
- The signature is cached per card content and key, so a stable card is signed once.

The signature header has `alg`, `typ` (`JOSE`), `kid`, and `jku` if set. Supported algorithms: `ES256`, `ES384`, `ES512`, `RS256`, `RS384`, `RS512`, `PS256`, `PS384`, `PS512`. `none` and `HS*` are not supported.

### One key per tenant

With [multi-tenancy](multi-tenancy.md), a <xref:A2A.Server.Agents.TenantCardProvider> can return a key for a tenant with `GetSigningKeyAsync`. Returning null uses the host key, or leaves the card unsigned if there is none. The tenant's key set is served at `/{tenant}/.well-known/jwks.json`.

[!code-csharp[](../snippets/AgentCardSigning.cs#TenantKey)]

## Verify on the client

A client trusts a key only if you tell it to. A key that the card points to by itself is never trusted. Configure <xref:A2A.Signing.CardTrustPolicy> through the resolver options.

Pin the public key for the agent's origin (`https://host[:port]`). Keys are matched by `kid`.

[!code-csharp[](../snippets/AgentCardSigning.cs#PinnedKey)]

Or allow a `jku` host for one origin. The key set is then fetched from that host, over HTTPS, and cached for 10 minutes (a failed fetch for 60 seconds).

[!code-csharp[](../snippets/AgentCardSigning.cs#JkuHost)]

`AllowedJkuHosts` is empty by default, which means `jku` is ignored, even on the card's own host. A host allowed for one origin does not verify cards from another. `jku` fetches go through the same SSRF guard as push webhooks (HTTPS only, no private addresses, no redirects, 10 second timeout, 256 KiB limit), and one verification fetches at most 5 key sets.

### Statuses

<xref:A2A.Signing.AgentCardVerificationStatus>:

| Status | Meaning |
|---|---|
| `Verified` | A signature verified with a trusted key. |
| `Unsigned` | The card has no signatures. |
| `Unverified` | There are signatures, but no trusted key applies. A forged card with its own `jku` ends here. |
| `Failed` | A trusted key applies and the signature does not verify, or a signature header is invalid. |

What the resolver does:

- `Failed` always stops resolution with <xref:A2A.Client.AgentCardVerificationException>.
- `Unsigned` and `Unverified` are rejected once `TrustedKeys` or `AllowedJkuHosts` is not empty. Set `Trust.RequireSignature = false` to accept them. An accepted unsigned card counts `a2a.card.unsigned`.
- With nothing configured, cards resolve and carry their status. Check `Verification.Status` yourself.
- For a `Verified` card, the model is built only from the signed content.

<xref:A2A.Signing.CardTrustPolicy.AllowedAlgorithms> defaults to all supported algorithms. <xref:A2A.Signing.CardTrustPolicy.AcceptedTyp> defaults to `JOSE` and `JOSE+JSON`. A `crit` header, a key that does not match the algorithm, and a missing `typ` give `Failed`.

## Resolve a card

[!code-csharp[](../snippets/AgentCardSigning.cs#Resolve)]

<xref:A2A.Client.ResolvedAgentCard> holds the card, the raw JSON, the source, the verification result, and `Identity`, which is `(origin, kid)`. Use that as the peer identity, not the card's `name`.

Text from a card is untrusted. `Description` and each skill's name, description and examples are <xref:A2A.Client.Cards.UntrustedText>. `ToString()` removes control, bidi, zero-width and tag characters and cuts the text to 2000 characters (<xref:A2A.Client.A2ACardResolverOptions.MaxCardTextLength>). `Raw` is the text as received. Use the sanitized form when you show it or put it in a prompt.

## Card pinning

Separate from signatures, the resolver pins the SHA-256 of each origin's canonical card. If the content changes, <xref:A2A.Client.Cards.PinPolicy> decides what happens:

| Policy | On a changed card |
|---|---|
| `Notify` (default) | Raises <xref:A2A.Client.A2ACardResolver.CardChanged>, logs a warning, counts `a2a.card.changed`, accepts the card and re-pins. |
| `Strict` | The same, then throws <xref:A2A.Client.AgentCardPinException>. The pin does not move. |
| `Off` | No pinning. |

Pins live in an <xref:A2A.Client.Cards.ICardPinStore>. The default is in memory, so pins are lost on restart. Pinning detects a change. It does not stop a forged card; trusted keys do that.

The hash covers the canonical form, so a change in a field outside the signed content is not a change.

## What the signature covers

The signed payload is the card reduced to the A2A v1.0 card schema, with empty values removed, then canonicalized with RFC 8785 (JCS). This matches a2a-python 1.2.1 and a2a-js 1.3.0.

- Members that are not in the v1.0 schema are **not covered**. This includes fields from newer versions, extensions, and v0.3 compatibility fields.
- Empty values (`null`, `""`, `[]`, `{}`) are removed, so they are not covered. `false` is removed from `AgentExtension.required` and `pkceRequired`. Other booleans keep `false`.
- `signatures` is not part of the payload.
- The server logs a warning once for each set of pruned paths it finds, and still signs.
- A verified card with a member that matches a v1.0 field only when case is ignored fails verification.

Do not trust anything in a signed card that the signature does not cover. The resolver builds the model from the signed content only, so such fields never reach your code.

## Key rotation

Keys are matched by `kid`, and a `TrustedKeys` set can hold several keys. To rotate:

1. Add the new public key to the clients' trusted set, next to the old one.
2. Switch the server to the new key with a new `kid`.
3. Remove the old key from the clients later.

[!code-csharp[](../snippets/AgentCardSigning.cs#Rotation)]

The server's key set shows only the current key, so clients that use `jku` pick up the new key after their cache expires (10 minutes). A key change with the same card content is not a card change. The pin keeps the hash and records the new `kid`.

## What can go wrong

- `Unverified` with "no trusted key": the `kid` is not in `TrustedKeys` for that origin. Check the origin spelling and the `kid`.
- `Unverified` with "jku host ... is not allowed": add the host with `AllowJkuHost`.
- `Failed` with "does not verify with trusted key": the client has a key with that `kid`, but it is not the public half of the server's private key. Or the card changed after it was signed.
- A key of another curve: the verifier rejects a key whose curve does not match `alg` (`ES256` needs P-256).

## See also

- [Multi-tenancy](multi-tenancy.md)
- [Push notifications](push-notifications.md) (the same SSRF guard)
- [Interop and conformance](interop-and-conformance.md)
- [Spec decisions](spec-decisions.md)
