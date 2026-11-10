# Cli sample

`walkthrough.sh` runs the `a2a` command line tool (`src/A2A.Cli`) against [EchoAgent](../EchoAgent/Program.cs) and
checks the exit code of each command. It starts EchoAgent on `127.0.0.1:5330` with a throwaway API key and stops it at
the end. It needs `curl` and `python3`.

```bash
samples/Cli/walkthrough.sh
```

| Command | Shows |
|---|---|
| `a2a card` | the Agent Card and its signature status. The fetch carries no credentials |
| `a2a send ping` | a direct reply (`pong`), no task |
| `a2a stream ...`, `a2a stream --json ...` | the events of a task as they arrive; NDJSON with `--json` |
| `a2a send --no-wait --json ...`, then `get`, `list`, `cancel` | a long task: start it, look at it, cancel it |
| `a2a get no-such-task` | exit code 5 (not found) |
| `a2a send ping` with no key | exit code 4: the card asks for an API key and none was given |

The agent URL and the key go in `A2A_AGENT_URL` and `A2A_API_KEY`. Options show in process lists and shell history.
`--allow-http` is needed for an `http://` URL; the tool still sends the key only over HTTPS or to a loopback address.
The script runs the tool with `dotnet run --project src/A2A.Cli`. Installed as a tool
(`dotnet tool install -g RomanAmbinder.A2A.Cli`), each `a2a ...` line is the same command.

Expected output ends with `OK: card, send, stream, get, list and cancel worked, and errors gave their exit codes.`

See the [command line tool guide](../../docs/site/guides/cli.md).
