using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using A2A.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Server.TypedSkills;

/// <summary>
/// The typed skills of one agent, and the optional fallback executor for every other request. Build it in
/// <see cref="TypedSkillsServerBuilderExtensions.AddTypedSkills"/>.
/// </summary>
[global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP001", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class TypedSkillSet
{
    private readonly Dictionary<string, TypedSkill> _skills = new(StringComparer.Ordinal);
    private Func<IServiceProvider, IAgentExecutor>? _fallback;
    private Action<IServiceCollection>? _register;

    /// <summary>Creates an empty set.</summary>
    public TypedSkillSet()
    {
    }

    internal int Count => _skills.Count;

    internal void Register(IServiceCollection services) => _register?.Invoke(services);

    internal bool Has(string id) => _skills.ContainsKey(id);

    internal IAgentExecutor? CreateFallback(IServiceProvider services) => _fallback?.Invoke(services);

    /// <summary>Adds a typed skill. Its schemas are made now, once.</summary>
    /// <typeparam name="TInput">The input type. Callers send it as one data part.</typeparam>
    /// <typeparam name="TOutput">The output type. It becomes the data part of the result artifact.</typeparam>
    /// <param name="id">The skill id. It must be the <see cref="AgentSkill.Id"/> clients use. Not empty.</param>
    /// <param name="inputType">Type information for <typeparamref name="TInput"/>, from a source-generated JSON context.</param>
    /// <param name="outputType">Type information for <typeparamref name="TOutput"/>, from a source-generated JSON context.</param>
    /// <param name="handler">Runs the skill. Return the output, or throw <see cref="TypedSkillFailedException"/> to fail with a message.</param>
    /// <param name="configure">Sets the card text and the validator, or null.</param>
    /// <returns>This set.</returns>
    /// <exception cref="ArgumentException">The id is empty or already used.</exception>
    /// <exception cref="InvalidOperationException">A schema is nested too deeply to send on the Agent Card.</exception>
    public TypedSkillSet Map<TInput, TOutput>(string id, JsonTypeInfo<TInput> inputType, JsonTypeInfo<TOutput> outputType,
        Func<TypedSkillContext<TInput>, CancellationToken, Task<TOutput>> handler, Action<TypedSkillOptions<TInput>>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(inputType);
        ArgumentNullException.ThrowIfNull(outputType);
        ArgumentNullException.ThrowIfNull(handler);
        if (_skills.ContainsKey(id)) throw new ArgumentException($"A typed skill with id '{id}' is already mapped.", nameof(id));
        var options = new TypedSkillOptions<TInput>();
        configure?.Invoke(options);
        _skills.Add(id, new TypedSkill<TInput, TOutput>(id, inputType, outputType, handler, options));
        return this;
    }

    /// <summary>
    /// Uses <typeparamref name="T"/> for every request that does not go to a typed skill (see the routing table in the guide). It is
    /// registered as a scoped service (unless you registered it), so it can take services in its constructor and is disposed with the execution scope. It also gets CancelTask for its own tasks.
    /// </summary>
    /// <typeparam name="T">The fallback executor.</typeparam>
    /// <returns>This set.</returns>
    public TypedSkillSet UseFallback<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, IAgentExecutor
    {
        // Registered scoped, so the execution scope owns the fallback and disposes it.
        _register = static services => services.TryAddScoped<T>();
        _fallback = static services => services.GetRequiredService<T>();
        return this;
    }

    /// <summary>
    /// Writes the skills to <paramref name="card"/>. It adds a missing <see cref="AgentSkill"/> and keeps the text an author set on an
    /// existing one. It adds <c>application/json</c> to the skill's input and output modes, and adds the skill-selection extension and
    /// the typed-skills extension (with the schemas) when they are missing. Calling it again changes nothing more.
    /// </summary>
    /// <param name="card">The card to change.</param>
    /// <exception cref="InvalidOperationException">The card has signatures: a change would break them. Add the skills before you sign.</exception>
    public void ApplyTo(AgentCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.Signatures is { Count: > 0 })
            throw new InvalidOperationException("The Agent Card is signed, and typed skills change it. Add the skills to the card before you sign it.");

        foreach (var skill in _skills.Values)
        {
            var target = card.Skills.Find(s => string.Equals(s.Id, skill.Id, StringComparison.Ordinal));
            if (target is null) card.Skills.Add(target = new AgentSkill { Id = skill.Id });
            if (string.IsNullOrEmpty(target.Name)) target.Name = skill.Name;
            if (string.IsNullOrEmpty(target.Description)) target.Description = skill.Description;
            if (target.Tags.Count == 0) target.Tags.AddRange(skill.Tags);
            if ((target.Examples is null || target.Examples.Count == 0) && skill.Examples.Count > 0) target.Examples = [.. skill.Examples];
            target.InputModes = WithJson(target.InputModes);
            target.OutputModes = WithJson(target.OutputModes);
        }

        var extensions = card.Capabilities.Extensions ??= [];
        if (!extensions.Exists(e => e.Uri == SkillSelection.ExtensionUri)) extensions.Add(SkillSelection.CreateExtension());
        var typed = extensions.Find(e => e.Uri == TypedSkillExtension.ExtensionUri);
        if (typed is null)
        {
            extensions.Add(typed = new AgentExtension
            {
                Uri = TypedSkillExtension.ExtensionUri,
                Required = false,
                Description = "JSON Schema of the input and output of typed skills.",
            });
        }
        typed.Params ??= [];
        typed.Params[TypedSkillExtension.SkillsParam] = SchemasElement(typed.Params);
    }

    private JsonElement SchemasElement(Dictionary<string, JsonElement> existing)
    {
        // Keep the entries of skills this set does not own.
        var skills = existing.TryGetValue(TypedSkillExtension.SkillsParam, out var current) && current.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(current.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        foreach (var skill in _skills.Values)
        {
            skills[skill.Id] = new JsonObject
            {
                [TypedSkillExtension.InputSchemaKey] = skill.InputSchema.DeepClone(),
                [TypedSkillExtension.OutputSchemaKey] = skill.OutputSchema.DeepClone(),
            };
        }
        using var document = JsonDocument.Parse(skills.ToJsonString());
        return document.RootElement.Clone();
    }

    private static List<string> WithJson(List<string>? modes)
    {
        modes ??= [];
        if (!modes.Contains("application/json", StringComparer.OrdinalIgnoreCase)) modes.Add("application/json");
        return modes;
    }

    /// <summary>
    /// Finds where a request goes. Null means the fallback. Throws InvalidParams when it goes nowhere. See the routing table in the guide.
    /// </summary>
    internal TypedSkill? Resolve(Message message, ServerCallContext call)
    {
        // Only a skill id the caller activated counts, as everywhere else (the S35 rule).
        var id = call.ActivatedExtensions.Contains(SkillSelection.ExtensionUri) ? message.GetRequestedSkillId() : null;
        var hasTask = !string.IsNullOrEmpty(message.TaskId);
        if (id is not null && _skills.TryGetValue(id, out var named))
            return hasTask ? throw TypedSkillRefusal.Refuse("A typed skill runs once and cannot continue a task.") : named;
        if (id is null && !hasTask && _fallback is null && _skills.Count == 1) return _skills.Values.First();
        if (_fallback is not null) return null;
        throw TypedSkillRefusal.Refuse(id is null
            ? "Name a skill of this agent in metadata.skillId."
            : "metadata.skillId does not name a skill of this agent.");
    }
}
