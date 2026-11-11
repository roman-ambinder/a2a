# TenantAgent sample

One host serves two tenants, `sales` and `support`. Each tenant has its own Agent Card and its own agent. Two demo
users call it with an in-process client. The server checks the tenant and the skill of every call, and writes each
refusal to the audit trail. The sample reads the trail back and checks it. It listens on `127.0.0.1:5340` only.

| Feature | Where | Guide |
|---|---|---|
| Tenants in routes | `MapA2A("/{tenant}/a2a")`, `MapAgentCard()` | [Multi-tenancy](../../docs/site/guides/multi-tenancy.md) |
| A card and an agent per tenant | `TenantCards` (`TenantCardProvider`), `TenantAgents` (`AgentExecutorResolver`) | [Multi-tenancy](../../docs/site/guides/multi-tenancy.md) |
| API keys, one tenant each | `AddApiKeyAuthentication(o => o.Add("alice", hash, "sales"))` | [Authentication and ownership](../../docs/site/guides/auth-and-ownership.md#api-keys) |
| Who may use a tenant | each key's tenants (the `a2a:tenant` claim), checked by the server before `TenantRules` | [Multi-tenancy](../../docs/site/guides/multi-tenancy.md) |
| Who may use a skill | `SkillAuthorizationMode.Required`, `TenantRules.AuthorizeSkillAsync` | [Skill authorization](../../docs/site/guides/skill-authorization.md) |
| Audit trail | `MemoryAuditSink` (`A2AAuditSink`), read back at the end | [Audit trail](../../docs/site/guides/audit.md) |

The demo users:

| User | Key | Tenant | May use |
|---|---|---|---|
| alice | made at startup (`A2AApiKey.Create()`) | sales | `summarize`, `translate` |
| bob | made at startup | support | `summarize` |

The server stores only the hashes of the keys. The task owner of each user is `apikey:alice` and `apikey:bob`.

The sample runs these calls:

1. bob runs `summarize` in `support`. A task is created.
2. alice asks for bob's task in `sales`. Tasks are stored by tenant and owner, so the answer is `TaskNotFound`.
3. alice asks for it in `support`. Her key may only call `sales`, so the server refuses before `TenantRules` runs. The
   caller sees `TaskNotFound`, so the answer does not confirm that the task exists. The audit trail gets an
   `AccessDenied` row for `GetTask` with the reason `TenantNotAllowed`.
4. bob runs `translate`. The server refuses with `PermissionDenied` before the agent runs. The audit trail gets an
   `AccessDenied` row for `SendMessage`.
5. alice runs `translate` in `sales`. It is allowed.

```bash
dotnet run --project samples/TenantAgent
```

Expected output ends with (task ids differ on each run):

```text
--- audit trail
TaskCreated Succeeded actor=apikey:bob tenant=support op= task=tyWuD59p92QSvsA1QOV0rg reason=
AccessDenied Denied actor=apikey:alice tenant=support op=GetTask task=tyWuD59p92QSvsA1QOV0rg reason=TenantNotAllowed
AccessDenied Denied actor=apikey:bob tenant=support op=SendMessage task= reason=PermissionDenied
TaskCreated Succeeded actor=apikey:alice tenant=sales op= task=pdjWm5fg7BJRmLaoMXZi8w reason=
OK: tenant sales could not see a support task, an unauthorized skill was refused, and the audit trail recorded both.
```

The keys are made in process for the demo. A real host gives each caller its key once (`a2a keys new`), keeps only the
hashes (in configuration or an `IApiKeyStore`), and uses a durable audit sink such as the Postgres one.
