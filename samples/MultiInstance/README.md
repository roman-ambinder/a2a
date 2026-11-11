# Multi-instance sample

One A2A agent app, started twice, sharing Postgres and Redis. A task started on instance A can be read, followed and cancelled through instance B.

| Part | What it shows |
|---|---|
| `Agent/` | `AddA2AServer` + `AddPostgresStores` + `AddRedisEventBus` + `AddA2ARetention`. A slow task (30 one-second steps). |
| `Client/` | Starts a task on A, then `GetTask`, `SubscribeToTask` and `CancelTask` through B. |
| `docker-compose.yml` | `postgres:16.4-alpine`, `redis:7.4-alpine`, `agent-a` (127.0.0.1:5101), `agent-b` (127.0.0.1:5102); ports are bound to localhost only. |

```bash
docker compose -f samples/MultiInstance/docker-compose.yml up --build -d
dotnet run --project samples/MultiInstance/Client      # [urlA] [urlB] [apiKey]
docker compose -f samples/MultiInstance/docker-compose.yml down -v
```

Expected output ends with `OK: a task started on A was read, followed and cancelled through B.`

Notes:
- The API key (a public demo key in `samples/Commons/Consts.cs`; the agents get only its hash, `ApiKeyHash` in `docker-compose.yml`) and the database password are demo values. Make a real key and its hash with `a2a keys new`.
- Retention is opt-in: `AddA2ARetention()` is what purges old tasks and fails tasks whose instance died.
- ASP.NET Core logs a Data Protection warning in the containers. The sample does not use push secrets. If you do, persist and share the key ring (`AddDataProtection()` + `AddA2ADataProtectionPushSecrets()`).
