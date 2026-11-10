using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using A2A.Json;

namespace A2A.Server.TypedSkills;

/// <summary>One typed skill: how to check its input, run its handler and describe it on the card.</summary>
internal abstract class TypedSkill
{
    // Room for the card, the capabilities, the extension entry, its params and the skills map around a schema.
    private const int CardNesting = 8;

    protected TypedSkill(string id, string name, string description, List<string> tags, List<string> examples,
        JsonNode inputSchema, JsonNode outputSchema)
    {
        Id = id;
        Name = name;
        Description = description;
        Tags = tags;
        Examples = examples;
        InputSchema = inputSchema;
        OutputSchema = outputSchema;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public List<string> Tags { get; }
    public List<string> Examples { get; }
    public JsonNode InputSchema { get; }
    public JsonNode OutputSchema { get; }

    /// <summary>The problems with the input of <paramref name="message" />; null when it is fine.</summary>
    public abstract IReadOnlyList<TypedSkillValidationError>? Check(Message message);

    public abstract Task RunAsync(AgentRequestContext request, TaskUpdater updater, IServiceProvider services,
        CancellationToken cancellationToken);

    public static JsonNode ExportSchema(JsonTypeInfo typeInfo, string skillId)
    {
        var schema = typeInfo.GetJsonSchemaAsNode(new JsonSchemaExporterOptions
            { TreatNullObliviousAsNonNullable = true });
        if (Depth(schema) + CardNesting > A2AJson.MaxJsonDepth)
            throw new InvalidOperationException(
                $"The schema of a type of typed skill '{skillId}' is nested too deeply to send on the Agent Card.");
        return schema;
    }

    private static int Depth(JsonNode? node)
    {
        return node switch
        {
            JsonObject o => 1 + o.Select(p => Depth(p.Value)).DefaultIfEmpty(0).Max(),
            JsonArray a => 1 + a.Select(Depth).DefaultIfEmpty(0).Max(),
            _ => 0
        };
    }
}

internal sealed class TypedSkill<TInput, TOutput> : TypedSkill
{
    private const int MaxViolations = 20;
    private readonly Func<TypedSkillContext<TInput>, CancellationToken, Task<TOutput>> _handler;
    private readonly JsonTypeInfo<TInput> _inputType;
    private readonly JsonTypeInfo<TOutput> _outputType;
    private readonly Func<TInput, IEnumerable<TypedSkillValidationError>>? _validate;

    public TypedSkill(string id, JsonTypeInfo<TInput> inputType, JsonTypeInfo<TOutput> outputType,
        Func<TypedSkillContext<TInput>, CancellationToken, Task<TOutput>> handler, TypedSkillOptions<TInput> options)
        : base(id, options.Name ?? id, options.Description ?? string.Empty, options.Tags, options.Examples,
            ExportSchema(inputType, id), ExportSchema(outputType, id))
    {
        _inputType = inputType;
        _outputType = outputType;
        _handler = handler;
        _validate = options.Validate;
    }

    public override IReadOnlyList<TypedSkillValidationError>? Check(Message message)
    {
        return Parse(message, out _);
    }

    private List<TypedSkillValidationError>? Parse(Message message, out TInput input)
    {
        input = default!;
        DataPart? data = null;
        var count = 0;
        foreach (var part in message.Parts ?? [])
        {
            if (part is not DataPart found) continue; // text and file parts are ignored
            data ??= found;
            count++;
        }

        if (count != 1) return [new TypedSkillValidationError("message.parts", "Send exactly one data part.")];

        var element = data!.Data;
        var errors = new List<TypedSkillValidationError>();
        if (_inputType.Kind == JsonTypeInfoKind.Object && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in _inputType.Properties)
            {
                if (!property.IsRequired || HasProperty(element, property.Name)) continue;
                errors.Add(new TypedSkillValidationError("$." + property.Name, "This property is required."));
                if (errors.Count == MaxViolations) break;
            }

            if (errors.Count > 0) return errors;
        }

        TInput? value;
        try
        {
            value = element.Deserialize(_inputType);
        }
        catch (JsonException ex)
        {
            return
            [
                new TypedSkillValidationError(TypedSkillRefusal.SafePath(ex.Path, _inputType),
                    "The value is not valid for this field.")
            ];
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException
                                       or ArgumentException)
        {
            return [new TypedSkillValidationError("$", "The data does not fit the input type.")];
        }

        if (value is null) return [new TypedSkillValidationError("$", "The data must not be null.")];

        if (_validate is not null)
        {
            foreach (var error in _validate(value))
            {
                errors.Add(error);
                if (errors.Count == MaxViolations) break;
            }

            if (errors.Count > 0) return errors;
        }

        input = value;
        return null;
    }

    private bool HasProperty(JsonElement element, string name)
    {
        if (!_inputType.Options.PropertyNameCaseInsensitive) return element.TryGetProperty(name, out _);
        foreach (var member in element.EnumerateObject())
            if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public override async Task RunAsync(AgentRequestContext request, TaskUpdater updater, IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // The filter checked this already. Check again: the request can reach the executor another way.
        if (Parse(request.Message, out var input) is not null)
        {
            await updater.FailAsync("The input is not valid for this skill.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        TOutput output;
        try
        {
            output = await _handler(new TypedSkillContext<TInput>(input, request, services, updater), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TypedSkillFailedException ex)
        {
            await updater.FailAsync(ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (output is null) throw new InvalidOperationException($"The handler of typed skill '{Id}' returned null.");

        var result = JsonSerializer.SerializeToElement(output, _outputType);
        await updater.AddArtifactAsync([Part.FromData(result)], name: Id, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}