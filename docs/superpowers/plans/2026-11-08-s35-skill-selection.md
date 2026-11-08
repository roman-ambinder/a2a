# S35 client-directed skill selection: plan

Spec: `docs/superpowers/specs/2026-11-08-s35-skill-selection-design.md`. Read it; it binds. Branch: `claude/s35-skill-selection`.

## Global Constraints

- Build gates: `dotnet build A2A.slnx -c Release` must end with 0 warnings. PublicApiAnalyzers: add every new public member to the project's `PublicAPI.Unshipped.txt`. XML docs on every public member. `AnalysisLevel=latest-recommended`. Namespace rules in `tests/A2A.Tests/PackageRulesTests.cs` (namespace follows folder). Public `CancellationToken` parameters are named `cancellationToken`. No `InternalsVisibleTo` between shipped packages.
- Source-generated JSON only: no reflection serializer paths.
- Key is exactly `skillId`. URI is exactly `https://github.com/roman-ambinder/a2a/extensions/skill-selection/v1`. Card param is exactly `rejectUnknownSkills`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Other sessions work on the client send path. Do not change `SendMessageAsync` or `SendStreamingMessageAsync` in `A2AClient.cs`.
- Plain English in docs and XML comments. Short sentences.

## Task 1: Protocol model

**Files:** create `src/A2A.Protocol/Skills/SkillSelection.cs`; edit `src/A2A.Protocol/PublicAPI.Unshipped.txt`; create `tests/A2A.Tests/Skills/SkillSelectionTests.cs`.

Write this code (namespace `A2A.Skills`):

```csharp
using System.Text.Json;
using A2A.Json;

namespace A2A.Skills;

/// <summary>
/// Client-directed skill selection: a client names the skill it wants in <c>Message.metadata["skillId"]</c>
/// (A2A epic #1989). The id is a hint from the caller, not an authorization decision.
/// </summary>
public static class SkillSelection
{
    /// <summary>Metadata key that holds the skill id.</summary>
    public const string MetadataKey = "skillId";

    /// <summary>URI of the optional skill-selection extension.</summary>
    public const string ExtensionUri = "https://github.com/roman-ambinder/a2a/extensions/skill-selection/v1";

    /// <summary>Name of the extension param that makes the agent refuse a skill it does not have.</summary>
    public const string RejectUnknownSkillsParam = "rejectUnknownSkills";

    /// <summary>Creates the optional extension entry for an agent card. Clients need not support it.</summary>
    /// <param name="rejectUnknownSkills">
    /// True to refuse, with InvalidParams, a caller that activated the extension and names a skill not on the card. Default false.
    /// </param>
    public static AgentExtension CreateExtension(bool rejectUnknownSkills = false) => new()
    {
        Uri = ExtensionUri,
        Required = false,
        Description = "Lets a client name the skill it wants in message metadata.",
        Params = rejectUnknownSkills ? new() { [RejectUnknownSkillsParam] = A2AJson.SerializeToElement(true) } : null,
    };

    /// <summary>True when <paramref name="card"/> declares the extension with <see cref="RejectUnknownSkillsParam"/> set to true.</summary>
    public static bool RejectsUnknownSkills(AgentCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.Capabilities.Extensions is not { } declared) return false;
        foreach (var extension in declared)
        {
            if (!string.Equals(extension.Uri, ExtensionUri, StringComparison.Ordinal)) continue;
            return extension.Params is { } values
                && values.TryGetValue(RejectUnknownSkillsParam, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        return false;
    }
}

/// <summary>Read and write the requested skill on messages.</summary>
public static class SkillSelectionMessageExtensions
{
    /// <summary>
    /// The skill id in the message metadata. Null when the key is absent, or its value is empty or not a string. Does not throw on
    /// a bad shape.
    /// </summary>
    /// <remarks>The caller writes this value. Treat it as untrusted text, and do not use it for authorization.</remarks>
    public static string? GetRequestedSkillId(this Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Metadata is not { } metadata || !metadata.TryGetValue(SkillSelection.MetadataKey, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var id = value.GetString();
        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>Writes <paramref name="skillId"/> to the message metadata (created when null) and returns the same message.</summary>
    /// <exception cref="ArgumentException"><paramref name="skillId"/> is null or empty.</exception>
    public static Message SetRequestedSkillId(this Message message, string skillId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(skillId);
        message.Metadata ??= [];
        message.Metadata[SkillSelection.MetadataKey] = A2AJson.SerializeToElement(skillId);
        return message;
    }
}
```

`A2AJsonContext` has `string` registered but maybe not `bool`. Check. If `bool` is missing, add `[JsonSerializable(typeof(bool))]` to `src/A2A.Protocol/Json/A2AJsonContext.cs`, in the same list.

**Tests** (`A2A.Tests`, namespace `A2A.Tests`, xUnit, in the style of `tests/A2A.Tests/Delegation/ActorChainTests.cs`):
- `CreateExtension()` has the URI, `Required` false, `Params` null.
- `CreateExtension(rejectUnknownSkills: true)` makes `RejectsUnknownSkills` true.
- `RejectsUnknownSkills` is false with: no extensions, a different URI, the URI with no params, param `false`, param string `"true"`.
- Round trip: set then get returns the id. Set on a message with null metadata creates it. Set keeps other keys.
- Get returns null for: no metadata, no key, empty string, number, object, null JSON.
- Set with null or empty id throws `ArgumentException` (null gives `ArgumentNullException`, which derives from it).
- A message with `skillId` survives a JSON serialize and deserialize with `A2AJson`.

Run: `dotnet test tests/A2A.Tests -f net10.0 --filter FullyQualifiedName~SkillSelection`. Commit: `feat(protocol): add skill selection key and extension`.

## Task 2: Server validation and executor read

**Files:** create `src/A2A.Server/Pipeline/SkillSelectionValidator.cs`; edit `src/A2A.Server/A2ARequestHandler.cs` (one line in `PrepareAsync`); edit `src/A2A.Server/AgentExecutor.cs`; edit `src/A2A.Server/PublicAPI.Unshipped.txt`; create `tests/A2A.Tests/Skills/SkillSelectionServerTests.cs` and `tests/A2A.Grpc.Tests/GrpcSkillSelectionTests.cs`.

Validator, `internal static` in namespace `A2A.Server.Pipeline`:

```csharp
using System.Text.Json;
using A2A.Server.Agents;
using A2A.Skills;

namespace A2A.Server.Pipeline;

/// <summary>Refuses a selected skill the agent does not have, when both the caller and the agent opted in.</summary>
internal static class SkillSelectionValidator
{
    /// <summary>
    /// Runs only when the caller activated the extension and the card sets <see cref="SkillSelection.RejectUnknownSkillsParam"/>.
    /// A message without the key costs one dictionary lookup.
    /// </summary>
    public static async ValueTask ValidateAsync(Message message, ServerCallContext call, RequestCards requestCards, IAgentCardProvider cards, CancellationToken ct)
    {
        if (message.Metadata is not { } metadata || !metadata.TryGetValue(SkillSelection.MetadataKey, out var value)) return;

        // Resolve first: with a card per tenant, this is what fills ActivatedExtensions. A tenant with no agent is refused elsewhere.
        if (await requestCards.ResolveAsync(call, ct).ConfigureAwait(false) is not { } card) return;
        if (!call.ActivatedExtensions.Contains(SkillSelection.ExtensionUri) || !SkillSelection.RejectsUnknownSkills(card)) return;

        if (value.ValueKind != JsonValueKind.String) throw Refuse("metadata.skillId must be a string");
        var id = value.GetString();
        if (string.IsNullOrEmpty(id) || HasSkill(card, id)) return;

        if (card.Capabilities.ExtendedAgentCard == true && call.IsAuthenticated
            && await cards.GetExtendedAsync(call, card, ct).ConfigureAwait(false) is { } extended
            && HasSkill(extended, id)) return;

        throw Refuse("metadata.skillId does not name a skill of this agent");
    }

    private static bool HasSkill(AgentCard card, string id)
    {
        if (card.Skills is not { } skills) return false;
        foreach (var skill in skills)
            if (string.Equals(skill.Id, id, StringComparison.Ordinal)) return true;
        return false;
    }

    private static A2AException Refuse(string text) => new(A2AErrorCode.InvalidParams, text)
    {
        Metadata = new() { ["extension"] = SkillSelection.ExtensionUri },
    };
}
```

In `A2ARequestHandler.PrepareAsync`, add this right after the `await RequireSupportedMediaTypesAsync(...)` line:

```csharp
await SkillSelectionValidator.ValidateAsync(request.Message, call, _requestCards, _cards, ct).ConfigureAwait(false);
```

Add a `using A2A.Skills;` to `AgentExecutor.cs`, and in `AgentRequestContext`, after `UserText`:

```csharp
    /// <summary>
    /// The skill the client selected (<c>Message.metadata["skillId"]</c>). Null when the caller did not activate the
    /// skill-selection extension, or sent no usable value. To read the value without that check, use
    /// <c>Message.GetRequestedSkillId()</c>.
    /// </summary>
    /// <remarks>
    /// This is a hint from the caller. Unless the card opts in with <see cref="SkillSelection.CreateExtension"/>, it may name a skill
    /// the agent does not have. Do not use it for authorization.
    /// </remarks>
    public string? RequestedSkillId =>
        Call.ActivatedExtensions.Contains(SkillSelection.ExtensionUri) ? Message.GetRequestedSkillId() : null;
```

**Tests** (`SkillSelectionServerTests`, `[Theory, MemberData(nameof(Bindings))]` over `BindingTests.Bindings` as in `tests/A2A.Tests/Delegation/ActorChainServerTests.cs`). The test agent records `context.RequestedSkillId` and its call count. Change the host's card through the `configure` argument of `TestAgentHost.StartAsync`; read `tests/A2A.Tests/TestHost.cs` first (`DefaultCard()` sets `ExtendedAgentCard = true`, and the host sets its own `ExtendedAgentCardProvider`). Request the extension with `new A2ARequestOptions { Extensions = [SkillSelection.ExtensionUri] }`.

1. Activated, declared without opt-in, known id: the agent sees the id.
2. Activated, declared without opt-in, unknown id: the agent runs and sees the unknown id (no rejection).
3. Activated, opt-in, known id: the agent runs and sees the id.
4. Activated, opt-in, unknown id: `A2AException` with `InvalidParams`, the agent did not run, `ListTasksAsync` is empty.
5. Same as 4, on `SendStreamingMessageAsync`.
6. Activated, opt-in, `skillId` is a number: `InvalidParams`.
7. Activated, opt-in, `skillId` is `""`: accepted, `RequestedSkillId` null.
8. Opt-in on the card, caller did not send the header, unknown id: accepted, `RequestedSkillId` null (plain v1.0 client).
9. Extension not declared, header sent, id set: accepted, `RequestedSkillId` null, `context.Message.GetRequestedSkillId()` gives the id.
10. Opt-in, id only on the extended card, authenticated caller: accepted.
11. Opt-in, id only on the extended card, anonymous caller: `InvalidParams`, and the extended provider was not called. Use `requireAuth: false` and allow anonymous callers as other tests in the repo do.
12. Per-tenant cards (`TenantCardProvider`; see `tests/A2A.Tests` for an existing per-tenant test to copy setup from): a tenant whose card opts in refuses an unknown id, and a tenant whose card does not opt in accepts it.

Also one test that a refusal carries `Metadata["extension"]` equal to the URI where the binding preserves metadata, and that the activated URI is echoed in the `A2A-Extensions` response header on success (raw `HttpClient` call or an existing helper).

`GrpcSkillSelectionTests` (copy setup from `tests/A2A.Grpc.Tests/GrpcDelegationTests.cs`): opt-in with unknown id is `InvalidParams` and the agent did not run; opt-in with a known id reaches the executor through `RequestedSkillId`.

Run the filter for `SkillSelection` in both test projects. Commit: `feat(server): validate selected skill and expose it to executors`.

## Task 3: Client helper

**Files:** edit `src/A2A.Client/A2AClient.cs` (near `RefreshWithExtendedAgentCardAsync` only); edit `src/A2A.Client/PublicAPI.Unshipped.txt`; create `tests/A2A.Tests/Skills/SkillSelectionClientTests.cs`.

Add `using A2A.Skills;`, then a private field `private volatile bool _extendedCardLoaded;`, set it to true in `RefreshWithExtendedAgentCardAsync` right after `AgentCard = card;`, and add:

```csharp
    /// <summary>
    /// Selects a skill for one send: writes <paramref name="skillId"/> to the message metadata and requests the skill-selection
    /// extension. Pass the returned options to any send method.
    /// </summary>
    /// <param name="message">The message to send. It is changed and must be the one you send.</param>
    /// <param name="skillId">The <see cref="AgentSkill.Id"/> of the skill. Not empty.</param>
    /// <param name="options">Options for the call, or null for new ones. These options are changed and returned.</param>
    /// <returns>The options to pass with <paramref name="message"/>.</returns>
    /// <remarks>
    /// When <see cref="A2AClientOptions.ValidateCapabilities"/> is on and the card is known, a skill that is not on the card fails here,
    /// and nothing is sent. The check is skipped when the card declares an extended card that is not loaded yet
    /// (<see cref="RefreshWithExtendedAgentCardAsync"/>), because the public list is then incomplete. A plain A2A peer ignores the
    /// selection.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="skillId"/> is null or empty.</exception>
    /// <exception cref="A2AException">InvalidParams: the card does not list the skill.</exception>
    public A2ARequestOptions SelectSkill(Message message, string skillId, A2ARequestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(skillId);
        if (_options.ValidateCapabilities && AgentCard is { } card
            && (card.Capabilities.ExtendedAgentCard != true || _extendedCardLoaded)
            && !(card.Skills?.Any(s => string.Equals(s.Id, skillId, StringComparison.Ordinal)) ?? false))
        {
            throw A2AException.InvalidParams($"The Agent Card has no skill '{skillId}'.");
        }

        message.SetRequestedSkillId(skillId);
        options ??= new A2ARequestOptions();
        if (options.Extensions is null or { IsReadOnly: true }) options.Extensions = new List<string>(options.Extensions ?? []);
        if (!options.Extensions.Contains(SkillSelection.ExtensionUri)) options.Extensions.Add(SkillSelection.ExtensionUri);
        return options;
    }
```

**Tests:**
- Sets the metadata, adds the URI once on a second call with the same options, and returns the same options object.
- Null options give new options. A `string[]` in `Extensions` is replaced, keeps its entries, and gains the URI.
- Known card without an extended card, unknown id: `A2AException` `InvalidParams`, and the message metadata is unchanged.
- `ValidateCapabilities = false`, unknown id: no throw.
- No card: no throw.
- Card with `ExtendedAgentCard = true`, not loaded, unknown id: no throw. After `RefreshWithExtendedAgentCardAsync` (use a fake `IA2AClient`), unknown id throws and a skill only on the extended card does not.
- Null message, null or empty id: `ArgumentException` family.
- End to end on all bindings: with a `TestAgentHost` whose card lists the skill and declares the extension, `SelectSkill` plus `SendMessageAsync` reaches the executor's `RequestedSkillId`; and over streaming. Use the two-argument `new A2AClient(transport, options, card)` constructor or `host.Client(binding)`; read how the host builds clients first.

Run the filter. Commit: `feat(client): add SelectSkill helper`.

## Task 4: Docs

**Files:** edit `docs/site/guides/spec-decisions.md`, `docs/2026-11-07-a2a-gap-analysis.md`.

- `spec-decisions.md`: right after the existing `- **Extensions.**` bullet add a `- **Skill selection.**` bullet. Say: an optional extension from A2A epic #1989, `metadata["skillId"]` plus the URI from `SkillSelection.ExtensionUri`; `A2AClient.SelectSkill` on the client; `AgentRequestContext.RequestedSkillId` on the server; declare with `SkillSelection.CreateExtension()`; `rejectUnknownSkills: true` makes an agent refuse a skill not on its card with `InvalidParams`, only for callers who activated the extension; skills on the extended card count for authenticated callers; the id is a hint and not authorization; plain v1.0 peers ignore it. No C# fenced blocks: inline code only (`docs/site/README.md`).
- Gap analysis: after the table in section 1.5 ("Missing capabilities") add a line in the same form as the line 46 status: `Status: S35 is done (`SkillSelection`, `A2AClient.SelectSkill`, `AgentRequestContext.RequestedSkillId`; design: `docs/superpowers/specs/2026-11-08-s35-skill-selection-design.md`).`
- Roadmap item 24 (`Skill-selection and elicitation extension packages (S35, S38).`): add `S35 done, in the existing packages.` to the line.

Commit: `docs: describe skill selection`.
