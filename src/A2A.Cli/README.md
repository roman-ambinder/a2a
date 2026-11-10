# RomanAmbinder.A2A.Cli

`a2a` is a command line tool for A2A (Agent2Agent) agents. It is built on `A2A.Client`.

```bash
dotnet tool install -g RomanAmbinder.A2A.Cli
export A2A_AGENT_URL=https://agent.example.com/
a2a card                       # fetch and verify the Agent Card
a2a send "hello"               # send a message and wait
a2a stream "write a report"    # print events as they arrive
a2a get <taskId>               # show a task
a2a list --status working
a2a cancel <taskId>
a2a download <taskId> -o ./files
```

- `--agent <url>` (or `A2A_AGENT_URL`) is required. Options can come anywhere after the command.
- `--json` prints protocol objects as JSON (NDJSON for streams). Errors are one JSON line on standard error.
- Credentials: `--token` / `A2A_TOKEN`, `--api-key` / `A2A_API_KEY`, `--client-id` and `--client-secret` /
  `A2A_CLIENT_ID` and `A2A_CLIENT_SECRET`.
  Prefer the variables: options show in process lists. A credential is sent only when the card asks for one, only to the
  card's origin.
- Card trust: `--trust-jwks <file>`, `--trust-jku-host <host>`, `--require-signature`.
- Secrets are never printed. Text from the agent is cleaned of terminal escape and bidi characters. Downloads check size
  and `sha256`,
  and agent-chosen file names cannot leave the output folder.

Exit codes: 0 success, 1 local error, 2 usage, 3 network or timeout, 4 auth, 5 not found, 6 other A2A error,
7 card verification failed, 8 artifact integrity failed, 9 task failed, rejected or canceled, 10 task needs input or
auth,
130 cancelled with Ctrl+C.

Not covered: mutual TLS and gRPC.

See the [full guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/cli.md).
