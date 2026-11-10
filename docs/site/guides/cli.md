# Command line tool

`a2a` is a .NET tool for talking to any A2A agent from a terminal or a script. It is built on `A2A.Client`. Use it to look at an Agent Card, send a message, watch a task, and download artifact files.

## Install

```bash
dotnet tool install -g RomanAmbinder.A2A.Cli
a2a --version
```

The package ID has the `RomanAmbinder.` prefix like every package of this SDK. The command is `a2a`. The tool needs the .NET 10 runtime. Without the runtime, publish it as a native AOT binary (`dotnet publish src/A2A.Cli -c Release -r <rid> -p:PublishAot=true`).

A script that runs each command against a sample agent and checks its exit code is in [samples/Cli](https://github.com/roman-ambinder/a2a/blob/main/samples/Cli/README.md).

## Basics

```bash
a2a <command> [options] [arguments]
```

- Options can come anywhere after the command. `--name value` and `--name=value` both work.
- `--` ends the options. Use it for message text or a task id that starts with a dash.
- `--agent <url>` (short: `-a`) is required for every command. It can be the base URL of the agent or the full URL of its Agent Card. You can set `A2A_AGENT_URL` instead. A URL with a user name or password in it is refused.
- `a2a --help`, `a2a <command> --help` and `a2a --version` print help and the version.

```bash
export A2A_AGENT_URL=https://agent.example.com/
a2a card
a2a send "What is the weather in Berlin?"
```

## Commands

### card

Fetches the Agent Card and checks its signature. Shows the name, version, description, provider, interfaces, skills, the verification result (status, key id, reason) and how strong the peer identity is.

```bash
a2a card --agent https://agent.example.com/
a2a card --trust-jwks keys.json --require-signature
```

The card fetch never carries credentials, so `card` works for agents that need a login.

### send

Sends a message and waits for the result. The text is the one argument. Use `-` to read the text from standard input.

```bash
a2a send "Summarize the report"
echo "Summarize the report" | a2a send -
a2a send --context-id ctx-1 --task-id task-7 "Here is the answer you asked for"
a2a send --no-wait "Start the long job"
```

| Option | Meaning |
|---|---|
| `--context-id <id>` | Continue a conversation. |
| `--task-id <id>` | Send the message to an existing task, for example to answer an input-required question. |
| `--history-length <n>` | Ask for this many history messages in the answer. |
| `--no-wait` | Return at once and do not wait for the task to finish. |

### stream

Like `send`, but uses streaming and prints one line per event as it arrives. Needs an agent that supports streaming. It ends when the task finishes or waits for input or authorization.

```bash
a2a stream "Write a long report"
a2a stream --json "Write a long report" | jq .
```

### subscribe

Prints the events of a task that is already running. Like `stream`, it ends when the task finishes or waits for input or authorization.

```bash
a2a subscribe 0b3c4d5e-...
```

### get, list, cancel

```bash
a2a get <taskId> [--history-length n]
a2a list [--context-id id] [--status working] [--page-size 20] [--page-token t] [--include-artifacts]
a2a cancel <taskId>
```

`--status` takes the state name in any case: `submitted`, `working`, `completed`, `failed`, `canceled`, `input-required`, `rejected`, `auth-required`. `cancel` exits with 0 when the agent accepted the cancel, whatever state the task is in afterwards.

### download

Gets the task, then saves the file behind every URL part of its artifacts.

```bash
a2a download <taskId>
a2a download <taskId> --artifact report --output ./files --max-bytes 52428800
```

| Option | Meaning |
|---|---|
| `--artifact <id>` | Only this artifact. |
| `--output <dir>`, `-o` | Folder to save into. Default: the current folder. |
| `--max-bytes <n>` | Largest file accepted. Default 1 GiB. |
| `--force` | Replace files that already exist. Without it, an existing file is an error (exit 1) and nothing is downloaded. |
| `--allow-private-storage` | Let the storage URL be a private address or plain HTTP, for example a MinIO server in your own network. |

How it saves files:

- The file name comes from the agent, so it is not trusted. Folders are cut off (at `/` and `\`), control characters and leading dots are removed, characters that Windows does not allow become `_`, and names are capped at 200 characters. A part without a usable name is saved as `<artifactId>-<index>`. Two files with the same name get `-2`, `-3` before the extension.
- The file is first written to a temporary file in the output folder. It is moved into place only after the size and `sha256` metadata of the part were checked. On any failure the temporary file is deleted.
- Parts that are not URLs (text, data, raw bytes) are skipped and counted.
- If no part can be downloaded, the exit code is 5.
- The part URL is never printed. Presigned storage URLs are secrets.

## Options for every command

| Option | Meaning |
|---|---|
| `--agent <url>`, `-a` | Agent base URL or Agent Card URL. Env: `A2A_AGENT_URL`. |
| `--json` | Print JSON, see below. |
| `--timeout <seconds>` | Time limit. Default 100 seconds for one call and for the card fetch. Streams and downloads have no overall limit unless you give one. |
| `--retries <n>` | Retries after the first attempt of a call. |
| `--tenant <name>` | Tenant for the request. |
| `--transport jsonrpc\|rest` | Use only this binding. |
| `--allow-http` | Allow an `http://` agent URL. Credentials are still sent only over HTTPS, or to a loopback address. |

## Authentication

The tool sends a credential only when the Agent Card asks for one, and only to the origin of the card. Give the credentials as options or as environment variables. An option wins over the variable.

| Option | Environment variable | Used for |
|---|---|---|
| `--token <t>` | `A2A_TOKEN` | `Authorization: Bearer` for an HTTP bearer, OAuth2 or OpenID Connect scheme. |
| `--api-key <k>` | `A2A_API_KEY` | An API key scheme (header or cookie). |
| `--client-id <id>` | `A2A_CLIENT_ID` | OAuth2 client credentials flow. Needs the secret too. |
| `--client-secret <s>` | `A2A_CLIENT_SECRET` | OAuth2 client credentials flow. Needs the id too. |
| `--token-endpoint <url>` | none | Token URL, when the card has none on its own origin. |
| `--scope <s>` | none | OAuth2 scope. Can be repeated. |

Prefer environment variables. Options are visible to other users in the process list and are saved in your shell history.

```bash
export A2A_TOKEN="$(cat ~/.secrets/agent-token)"
a2a send "hello"
```

If a card needs a credential that you did not give, the command fails at once with exit code 4. If you give a credential and the card asks for none, the tool says so on standard error and sends nothing.

## Agent Card trust

By default a card is accepted with its status: `unsigned`, `unverified` or `verified`. These options set up signature checks. See [Agent card signing](agent-card-signing.md).

| Option | Meaning |
|---|---|
| `--trust-jwks <file>` | A JSON Web Key Set file. Its keys are trusted for this agent's origin. |
| `--trust-jku-host <host>` | A host that may serve the signing keys (`jku`). Can be repeated. |
| `--require-signature` | Fail with exit code 7 unless the card has a valid signature. |

Card pinning is off, because the tool is a single run.

## JSON output

With `--json`, the tool prints protocol objects as the agent sent them, in the A2A JSON format. Control characters are escaped by JSON.

- `send`, `get`, `cancel`, `list`: one JSON object, the response.
- `stream`, `subscribe`: NDJSON. One `StreamResponse` per line, flushed after each event.
- `card`: `{"source": "...", "verification": {"status": "unsigned", "keyId": null, "reason": null}, "card": { ... }}`.
- `download`: `{"files": [{"path": "...", "artifactId": "...", "bytes": 123}], "skipped": 1}`.
- Errors: one line on standard error, `{"error": {"exitCode": 5, "code": "TASK_NOT_FOUND", "message": "..."}}`. `code` is the A2A error reason, or `null` when the error is not an A2A error.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | Unexpected or local error, for example a file that already exists. |
| 2 | Usage error: unknown option, missing value, bad number, wrong number of arguments. |
| 3 | Network or connection error, or `--timeout` ran out. |
| 4 | Authentication or authorization error (HTTP 401 or 403, or a missing credential). |
| 5 | Not found: the task does not exist, or no artifact matches. |
| 6 | Another A2A or protocol error from the agent. |
| 7 | Agent Card verification or pin failure. |
| 8 | Artifact integrity, size or origin failure. |
| 9 | The task ended failed, rejected or canceled (`send`, `stream`, `subscribe`). |
| 10 | The task waits for input or authorization (`send`, `stream`, `subscribe`). |
| 130 | You cancelled with Ctrl+C. |

Errors go to standard error. A stack trace is never printed.

## Security notes

- Secret values (token, API key, client secret) are replaced with `***` in everything the tool prints, also when the agent echoes them. The tool never prints its own options. OAuth2 access tokens that the tool gets from a token endpoint are not known in advance and are not replaced.
- Text from the agent is cleaned before it is shown in the terminal: control characters (such as escape sequences), bidi controls and invisible characters are removed. This includes error messages from the server.
- In error messages, the query string and user info of every URL are removed, because presigned URLs are secrets.
- Credentials go only to the card's origin, never follow a redirect, and never go over plain HTTP to a host that is not loopback. `--allow-http` only lets you fetch an HTTP card.
- Downloads go to the agent's origin with your credentials. When the agent answers with a redirect to a storage URL, that URL is fetched once, without credentials, and only if it is HTTPS and not a private address (unless you give `--allow-private-storage`).

## Not covered

- Mutual TLS credentials.
- The gRPC binding. The tool uses JSON-RPC or HTTP+JSON. A card with only a gRPC interface is refused.
- Push notification configuration, extended Agent Cards and the v0.3 protocol.
