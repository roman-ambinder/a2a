# Multi-instance sample

One A2A agent app, started twice, sharing Postgres and Redis. A task started on instance A can be read, followed and cancelled through instance B.

| Part | What it shows |
|---|---|
| `Agent/` | `AddA2AServer` + `AddPostgresStores` + `AddRedisEventBus` + `AddA2ARetention`. A slow task (30 one-second steps). |
| `Client/` | Starts a task on A, then `GetTask`, `SubscribeToTask` and `CancelTask` through B. |
| `docker-compose.yml` | `postgres:16.4-alpine`, `redis:7.4-alpine`, `agent-a` (port 5101), `agent-b` (port 5102). |

```bash
docker compose -f samples/MultiInstance/docker-compose.yml up --build -d
dotnet run --project samples/MultiInstance/Client      # [urlA] [urlB] [apiKey]
docker compose -f samples/MultiInstance/docker-compose.yml down -v
```

Expected output ends with `OK: a task started on A was read, followed and cancelled through B.`

Notes:
- The API key (`demo-key-change-me`) and the database password are demo values.
- Retention is opt-in: `AddA2ARetention()` is what purges old tasks and fails tasks whose instance died.
- ASP.NET Core logs a Data Protection warning in the containers. The sample does not use push secrets. If you do, persist and share the key ring (`AddDataProtection()` + `AddA2ADataProtectionPushSecrets()`).
