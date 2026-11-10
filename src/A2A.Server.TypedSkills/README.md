# RomanAmbinder.A2A.Server.TypedSkills

Typed skills for an A2A (Agent2Agent) server. You declare a skill with .NET input and output types. The SDK:

- puts JSON Schema for both types on the Agent Card,
- refuses bad input with `InvalidParams` before the agent runs and before anything is stored,
- runs your handler with the checked input, and sends its result as a data artifact,
- gives clients a typed call: `A2AClient.CallSkillAsync` (in `RomanAmbinder.A2A.Client`).

It is AOT safe: every type comes as a `JsonTypeInfo<T>` from a `System.Text.Json` source-generated context. A plain
`IAgentExecutor` can run next to it as the fallback.

```csharp
builder.Services.AddA2A(a => a
    .AddAgentCard(card)
    .AddTypedSkills(skills => skills
        .Map("book", AppJson.Default.BookingRequest, AppJson.Default.BookingResult,
            (context, ct) => Task.FromResult(new BookingResult(context.Input.City, 1)),
            o => o.Description = "Books a room.")
        .UseFallback<ChatAgent>()));
```

The API is `[Experimental]` (`A2AEXP001`), like skill selection. See
the [guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/typed-skills.md) for the rules, the routing
table and the errors.
