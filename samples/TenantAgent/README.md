# TenantAgent sample

One host serves two tenants, `sales` and `support`. Each tenant has its own Agent Card and its own agent. Two demo
users call it with an in-process client. The server checks the tenant and the skill of every call, and writes each
refusal to the audit trail. The sample reads the trail back and checks it. It listens on `127.0.0.1:5340` only.

| Feature | Where | Guide |
|---|---|---|
| Tenants in routes | `MapA2A("/{tenant}/a2a")`, `MapAgentCard()` | [Multi-tenancy](../../docs/site/guides/multi-tenancy.md) |
| A card and an agent per tenant | `TenantCards` (`TenantCardProvider`), `TenantAgents` (`AgentExecutorResolver`) | [Multi-tenancy](../../docs/site/guides/multi-tenancy.md) |
| Who may use a tenant | `TenantRules.AuthorizeAsync` checks the caller's `tenant` claim | [Multi-tenancy](../../docs/site/guides/multi-tenancy.md) |
| Who may use a skill | `SkillAuthorizationMode.Required`, `TenantRules.AuthorizeSkillAsync` | [Skill authorization](../../docs/site/guides/skill-authorization.md) |
| Audit trail | `MemoryAuditSink` (`A2AAuditSink`), read back at the end | [Audit trail](../../docs/site/guides/audit.md) |

The demo users:

| User | Key (demo value) | Tenant | May use |
|---|---|---|---|
| alice | `demo-key-alice` | sales | `summarize`, `translate` |
| bob | `demo-key-bob` | support | `summarize` |

The sample runs these calls:

1. bob runs `summarize` in `support`. A task is created.
2. alice asks for bob's task in `sales`. Tasks are stored by tenant and owner, so the answer is `TaskNotFound`.
3. alice asks for it in `support`. `TenantRules` refuses: she is not a member. The caller sees `TaskNotFound`, so the
   answer does not confirm that the task exists. The audit trail gets an `AccessDenied` row for `GetTask`.
4. bob runs `translate`. The server refuses with `PermissionDenied` before the agent runs. The audit trail gets an
   `AccessDenied` row for `SendMessage`.
5. alice runs `translate` in `sales`. It is allowed.

```bash
dotnet run --project samples/TenantAgent
```

Expected output ends with (task ids differ on each run):

```text
--- audit trail
TaskCreated Succeeded actor=bob tenant=support op= task=KpsQMa3ZuAIipUHBkKB_DA reason=
AccessDenied Denied actor=alice tenant=support op=GetTask task=KpsQMa3ZuAIipUHBkKB_DA reason=PermissionDenied
AccessDenied Denied actor=bob tenant=support op=SendMessage task= reason=PermissionDenied
TaskCreated Succeeded actor=alice tenant=sales op= task=CH5fiuJelaPY3mV40CKE1Q reason=
OK: tenant sales could not see a support task, an unauthorized skill was refused, and the audit trail recorded both.
```

The `X-Api-Key` scheme and its keys are a demo. A real host uses a real authentication scheme, and a durable audit sink
such as the Postgres one.
