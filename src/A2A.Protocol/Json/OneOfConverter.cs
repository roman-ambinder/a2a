using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace A2A.Json;

/// <summary>
/// Base converter for a closed union that is a JSON object with exactly one of a fixed set of members (a proto oneof).
/// Zero or more than one known member is a <see cref="JsonException"/> that names the members found. Unknown members are
/// skipped; members set to <c>null</c> count as absent. Uses only reader/writer primitives plus <c>JsonTypeInfo</c> lookups
/// for the nested payloads, so it works with source-generated contexts.
/// </summary>
/// <typeparam name="TUnion">The abstract union type.</typeparam>
public abstract class OneOfConverter<TUnion> : JsonConverter<TUnion> where TUnion : class
{
    /// <summary>Name used in error messages, for example <c>StreamResponse</c>.</summary>
    protected abstract string UnionName { get; }

    /// <summary>The wire names of the members, in documentation order.</summary>
    protected abstract IReadOnlyList<string> MemberNames { get; }

    /// <summary>Reads the payload of <paramref name="member"/> (the reader is on the payload's first token) into its case.</summary>
    protected abstract TUnion ReadCase(ref Utf8JsonReader reader, string member, JsonSerializerOptions options);

    /// <summary>Writes the single property (name and payload) for <paramref name="value"/>.</summary>
    protected abstract void WriteCase(Utf8JsonWriter writer, TUnion value, JsonSerializerOptions options);

    /// <inheritdoc />
    public sealed override TUnion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"{UnionName} must be a JSON object");

        TUnion? result = null;
        string? first = null; // the first member found
        List<string>? more = null; // members after the first: only for the error message
        var names = MemberNames;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            // The member's own name instance, matched on the reader's bytes: no string per property.
            var name = MemberAt(ref reader, names);
            reader.Read();
            if (name is null || reader.TokenType == JsonTokenType.Null)
            {
                reader.Skip();
                continue;
            }
            if (first is null)
            {
                first = name;
                result = ReadCase(ref reader, name, options);
            }
            else
            {
                (more ??= [first]).Add(name);
                reader.Skip();
            }
        }

        if (first is null)
            throw new JsonException($"{UnionName} must set exactly one of {string.Join(", ", names)}; found none");
        if (more is not null)
            throw new JsonException($"{UnionName} must set exactly one of {string.Join(", ", names)}; found: {string.Join(", ", more)}");
        return result!;
    }

    /// <inheritdoc />
    public sealed override void Write(Utf8JsonWriter writer, TUnion value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        WriteCase(writer, value, options);
        writer.WriteEndObject();
    }

    /// <summary>The member whose name the reader is on (a property name), or null for an unknown property.</summary>
    private static string? MemberAt(ref Utf8JsonReader reader, IReadOnlyList<string> names)
    {
        for (var i = 0; i < names.Count; i++)
            if (reader.ValueTextEquals(names[i])) return names[i];
        return null;
    }

    /// <summary>
    /// Reads a nested payload. The single place the union converters call back into the serializer, so a source-generated
    /// resolver only has to serve <typeparamref name="T"/> through <c>options.GetTypeInfo</c>.
    /// </summary>
    protected static T ReadPayload<T>(ref Utf8JsonReader reader, JsonSerializerOptions options) where T : class =>
        JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)))
            ?? throw new JsonException($"{typeof(T).Name} payload must not be null");

    /// <summary>Writes <paramref name="name"/> followed by the serialized payload.</summary>
    protected static void WriteProperty<T>(Utf8JsonWriter writer, string name, T payload, JsonSerializerOptions options) where T : class
    {
        writer.WritePropertyName(name);
        JsonSerializer.Serialize(writer, payload, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));
    }
}

/// <summary>
/// Converter for one concrete case of a union. System.Text.Json does not apply the base type's converter when the declared
/// type is a derived record, so each case names this converter. It shares the logic of <typeparamref name="TConverter"/>.
/// </summary>
/// <typeparam name="TUnion">The abstract union type.</typeparam>
/// <typeparam name="TCase">The case type.</typeparam>
/// <typeparam name="TConverter">The union's converter.</typeparam>
public sealed class OneOfCaseConverter<TUnion, TCase, TConverter> : JsonConverter<TCase>
    where TUnion : class
    where TCase : class, TUnion
    where TConverter : OneOfConverter<TUnion>, new()
{
    private static readonly TConverter Union = new();

    /// <inheritdoc />
    public override TCase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Union.Read(ref reader, typeof(TUnion), options) as TCase
            ?? throw new JsonException($"{typeof(TUnion).Name} content does not match {typeof(TCase).Name}");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TCase value, JsonSerializerOptions options) =>
        Union.Write(writer, value, options);
}
