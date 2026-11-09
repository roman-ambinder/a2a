# Security policy

## Report a vulnerability

Report it privately through GitHub: <https://github.com/roman-ambinder/a2a/security/advisories/new>

Do not open a public issue for a vulnerability.

Please include:

- The affected package and version.
- Steps to reproduce.
- The impact.

Reports are handled on a best-effort basis. There is no response-time guarantee.

## Supported versions

No version is released yet.

Once releases exist, security fixes go to the latest released minor version only. Before 1.0.0, a minor version may break the API and a patch never does ([versioning](docs/site/guides/versioning.md)). So a fix ships as a patch on that latest minor.

## In scope

The code in the `src/` packages (NuGet IDs `RomanAmbinder.A2A.*`). For example:

- A bypass of the SSRF guard in `A2A.Security.Http` (private addresses, redirects, size or time limits) for [push webhooks](docs/site/guides/push-notifications.md), `jku` key-set fetches or other peer-chosen URLs.
- A flaw in Agent Card signature verification (JWS or JCS), such as accepting a bad signature or an untrusted key. See [Agent Card signing](docs/site/guides/agent-card-signing.md).
- One caller reaching another caller's tasks, or one tenant reaching another tenant's tasks. See [authentication and ownership](docs/site/guides/auth-and-ownership.md) and [multi-tenancy](docs/site/guides/multi-tenancy.md).
- Artifact download links that expose data to the wrong caller, or serve stored content in a way that lets it run from the storage origin. See [artifact storage](docs/site/guides/artifact-storage.md).
- A bypass of the request and size limits. See [limits](docs/site/guides/limits-and-rate-limiting.md).
## Out of scope

- `samples/`, `interop/`, `conformance/`, `tests/` and `docs/`. They are not shipped. They are for demos or tests only.
- Bugs in third-party dependencies. Report them to their maintainers, unless this SDK's use of the dependency is the cause.
- Problems that need an insecure app configuration the docs warn about (for example `AllowAnonymous`).
- The A2A protocol specification itself.
