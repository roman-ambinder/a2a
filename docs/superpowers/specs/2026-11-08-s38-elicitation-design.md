# Structured elicitation for input-required (S38)

Date: 2026-11-08. Gap: S38 in `docs/2026-11-07-a2a-gap-analysis.md`.

## 1. Goal

An agent in `TASK_STATE_INPUT_REQUIRED` can ask for a small typed form, as well as free text. A client that activated the extension reads the form and answers with typed values, or declines, or cancels. The server checks the answer before the agent runs. A plain A2A v1.0 peer sees a normal text question and nothing else.

## 2. Prior art

- **A2A PR #2149** (closed unmerged 2026-09-22, read 2026-11-08) added `Task.elicitations`, states `WAITING`/`BLOCKED`/`RESOLVED` and a `SendMessageRequest.elicitationId` hint. It tracks *whether* input is outstanding. It has no form schema. No field landed in the proto, so an extension is the route, as the gap analysis says.
- **MCP elicitation** (2025-06-18 and draft) is the model for the form: `message` plus a flat `requestedSchema`, answered by `action` `accept`/`decline`/`cancel` with `content`. It forbids asking for secrets in a form. We keep the names `requestedSchema`, `action` and `content`, and a smaller keyword set.
- **S35 and approvals in this SDK** set the shape: constants and pure data in `A2A.Protocol`, a validator in `PrepareAsync`, a helper that changes and returns `A2ARequestOptions`. Approvals show how to bind an answer to one status message and use it once.

## 3. Decisions

**D1. Where it lives.** Existing packages, as S35 did. Model and validation in `A2A.Protocol`, namespace `A2A.Elicitations`. Server helper in `A2A.Server`. Client helper in `A2A.Client`. No new package: the client and the server share one validator, and it has no dependencies.

**D2. URI and key.** `Elicitation.ExtensionUri` = `https://github.com/roman-ambinder/a2a/extensions/elicitation/v1`. `Elicitation.MetadataKey` is the same string: the A2A way to namespace extension metadata. The SDK does not add the extension to cards by itself. `Elicitation.CreateExtension()` makes the optional card entry.

**D3. Metadata, not a data part.** A data part goes through the agent's input-mode check, so a `text/plain` agent would refuse an answer. It also shows up in a plain client's `parts`. Metadata has neither problem, and a form is about the message, not content of it.

**D4. Wire shape.** On the `TASK_STATE_INPUT_REQUIRED` status message, next to a text part:

```json
"metadata": { "<uri>": {
  "id": "f1",
  "requestedSchema": {
    "type": "object",
    "properties": {
      "city":   { "type": "string", "title": "City", "maxLength": 100 },
      "nights": { "type": "integer", "minimum": 1, "maximum": 30 },
      "room":   { "type": "string", "enum": ["single", "double"] },
      "late":   { "type": "boolean" }
    },
    "required": ["city", "nights"]
  } } }
```

On the follow-up user message, which has the task id and a text part:

```json
"metadata": { "<uri>": { "id": "f1", "action": "accept", "content": { "city": "Oslo", "nights": 2 } } }
```

`action` is `accept`, `decline` or `cancel`. `content` is present for `accept` only. The default text part of an answer says only which action was taken, and never repeats the values: one place to protect, not two.

**D5. Schema subset.**

- Top level: `type: "object"`, `properties`, optional `required`. Nothing else.
- Field: `type` is `string`, `integer`, `number` or `boolean`, with optional `title`, `description`.
- `string` adds `minLength`, `maxLength`, `format` (`email`, `date`, `date-time`) and `enum` (strings). `enum` and `format` do not combine.
- `integer` and `number` add `minimum`, `maximum` (inclusive).
- Left out on purpose: nesting, arrays, `default` (a peer-chosen value a user may submit without reading), `pattern` (ReDoS), `format: uri` (invites the agent to fetch a client-chosen URL) and `password`.
- A keyword outside this list makes the whole form unreadable. New keywords mean a new URI (`/v2`).

Caps, as constants on `Elicitation`: 32 fields, name 64 chars, title 200, description 1000, form id 128, 64 options, option value 256, string value 4096 (a `maxLength` may not exceed it). Field names match `[A-Za-z_][A-Za-z0-9_-]*`. Form ids match `[A-Za-z0-9._~-]+`.

**D6. Numbers.** gRPC carries metadata as `Struct`, so numbers are doubles. An `integer` is any finite number with no fraction, so `3.0` counts, with magnitude at most 2^53. `GetInt64` on the answer reads either form.

**D7. Activation.** The caller activates by `A2A-Extensions`, on the request that gets the form and on the request that answers it. The card must declare the extension (S31). Otherwise:

| Case | Result |
|---|---|
| Not activated, agent uses the form helper | Status message has the text part only. No metadata. |
| Not activated, answer key present | Ignored. `context.ElicitationAnswer` is null. |
| Activated, form pending, plain text reply | Accepted. `ElicitationAnswer` is null, agent reads `UserText`. |
| Activated, answer key present, no form pending | `InvalidParams`. |

The text part is required by the helper (not empty), so a plain client and a client that cannot read the form can always answer in words.

**D8. Server helper.** `TaskUpdater.RequireInputAsync(AgentRequestContext context, ElicitationForm form, string text, CancellationToken)`. It takes the context because activation is per call. `context.CanElicit` tells an agent if the form will reach the client.

**D9. Pending form and single use.** The pending form is the one in the status message of a task in `InputRequired`, so it is stored with the task and every instance sees it. Like approvals, a valid answer of any action records the status message id in `Task.metadata["a2a.elicitation.answered"]` (`Elicitation.AnsweredKey`) in the same compare-and-swap as the message. A used form is no longer pending. Asking again means a new status message, which is a new pending form, even with the same form id. A status message without an id fails closed.

**D10. Validation.** On the server inside the follow-up lock, after the state check and `EnsureTurnCapacity`, before the commit. A refusal is `InvalidParams` with `metadata.extension` = the URI, and `metadata.field` when one field is at fault. Nothing is stored and the agent does not run. The task stays waiting, so the client may correct and retry. Messages name the field and the rule, and never repeat the value.

Refused: wrong `id`, unknown `action`, `content` on decline or cancel, missing `content` on accept, non-object `content`, unknown property, missing required field, `null` value, wrong type, length, range, option or format, text that is not valid UTF-16. Lengths count code points, as JSON Schema does. `ElicitationForm.Validate` does this work, and the client helper calls the same code to fail fast.

**D11. Accept, decline, cancel.** `accept`: the user sent values. `decline`: the user refuses this request. `cancel`: the user dismissed it without a decision. The server moves no state for any of them: the agent decides what comes next, for example `FailAsync`, a new form, or carrying on without the data. This is not CancelTask.

**D12. Agent read.** `context.ElicitationAnswer` is a validated `ElicitationAnswer` (`FormId`, `Action`, `Content`, typed getters) or null. It is what the user typed: untrusted, and not an authorization decision.

**D13. Client helper.**

- `A2ARequestOptions.UseElicitation()` adds the URI once. Same rules as `SelectSkill`: changes and returns the options, replaces a read-only list.
- `Message.GetElicitationForm()` and `AgentTask.GetElicitationForm()` (the latter only in `InputRequired`) return null for an absent or malformed form. They never throw.
- `ElicitationAnswer.Accept(form, values, allowSensitive: false)`, `.Decline(form)`, `.Cancel(form)`. `Accept` validates locally.
- `IA2AClient.AnswerElicitationAsync(task, answer, options, ct)` sends it. For streaming, use `answer.ToMessage(taskId, contextId)` with `UseElicitation`.

**D14. Combining with existing guards.**

- `OnMessageReceived` runs on the status message with the form in `metadata`, on task snapshots and status updates alike, so a hook can read the form with `GetElicitationForm()` and throw before the application shows it. The answer goes out through `SendMessageAsync`, so the hook also sees the agent's next message.
- `MaxTurnsPerTask`: an accepted answer of any action is one turn. A refused answer is not stored, so it uses no turn. At the cap the follow-up is `UnsupportedOperation`, and that check runs first. An agent that may need a form must leave one turn for the answer.

## 4. Safety

**The form is peer input.** On read, `title` and `description` go through `TextSanitizer` and the caps. An option value that the sanitizer would change makes the form unreadable, because the answer must echo it exactly. The fallback text is the agent's words too, and gets no special handling. Apps must show the user which agent is asking (`AgentCard` origin, not its `name`) and never fill a form without the user. An LLM must not fill a form from its own context without a person, or a hostile agent can pull data out of that context.

**Secrets are refused by default, on both sides.** A field looks sensitive when its name, title or description has one of these words, after splitting on case changes and non-alphanumerics: `password`, `passwd`, `pwd`, `passphrase`, `passcode`, `secret`, `credential`, `credentials`, `token`, `apikey`, `privatekey`, `cvv`, `cvc`, `ssn`, `pin`, `otp`, `iban`; or these pairs: `api key`, `private key`, `access key`, `card number`, `credit card`, `debit card`, `security code`, `social security`.

- Server: `new ElicitationForm(...)` throws `ArgumentException` for such a field unless `allowSensitiveFields: true`.
- Client: `field.LooksSensitive` is exposed on read. `Accept` throws `InvalidParams` for a value in such a field unless `allowSensitive: true`. An omitted optional field passes. The client decides alone: the opt-out is not on the wire, so a server cannot grant it.

The list is a guard against mistakes and simple attacks. It cannot catch every wording. The real rule, in the docs: do not collect secrets here. Answers are stored in the task history, sent to webhooks with the task, and readable by whoever can read the task. Use the auth-required flow for credentials. The SDK does not put form values in logs or spans.

## 5. Non-goals

Several forms per status (PR #2149's model), non-blocking requests, URL mode, nested objects, multi-select, defaults, v0.3 compat surface, wiring forms into `A2A.Extensions.AI`.

## 6. Wire compatibility

A request or status without the key is unchanged. A call that did not activate the extension gets no form on the status message it causes. A form stored by an activated run is visible to anyone who may read the task, and plain peers ignore it. `Task.metadata` gains a key only after a valid answer. Cards change only if the host lists the extension. Parity tests and `interop/run.sh` stay as they are.
