using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using A2A.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Lf.A2a.V1;

namespace A2A.Grpc.Tests;

/// <summary>
///     Spec §1.4: SDK models MUST follow the proto. The gRPC types are generated from <c>docs/a2a.proto</c>, but the JSON
///     model in <c>A2A.Protocol</c> is written by hand, so these tests compare the two: a message, field, union case or enum
///     value added to the proto fails here until the model has it.
/// </summary>
public class ProtoModelDriftTests
{
    /// <summary>Proto messages whose model type has another name.</summary>
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.Ordinal)
    {
        ["Task"] = "AgentTask",
        ["TaskStatus"] = "AgentTaskStatus",
        ["APIKeySecurityScheme"] = "ApiKeySecurityScheme",
        ["HTTPAuthSecurityScheme"] = "HttpAuthSecurityScheme",
    };

    private const string TaskJson = """{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING"}}""";
    private const string MessageJson = """{"messageId":"m1","role":"ROLE_AGENT","parts":[{"text":"hi"}]}""";

    /// <summary>
    ///     The messages read through a custom converter (unions), with one sample value per proto field. A field without a
    ///     sample fails <see cref="Every_field_of_a_union_message_has_a_sample" />. <c>Base</c> is merged into every sample
    ///     of a field outside the oneof, so the object stays valid.
    /// </summary>
    private static readonly Dictionary<string, (Type Model, string? Base, Dictionary<string, string> Samples)> Unions = new()
    {
        ["Part"] = (typeof(Part), """{"text":"hi"}""", new()
        {
            ["text"] = "\"hi\"",
            ["raw"] = "\"aGk=\"",
            ["url"] = "\"https://files.example/a.txt\"",
            ["data"] = """{"a":1,"b":[true,null,"x"]}""",
            ["metadata"] = """{"k":"v","n":2}""",
            ["filename"] = "\"a.txt\"",
            ["mediaType"] = "\"text/plain\"",
        }),
        ["SecurityScheme"] = (typeof(SecurityScheme), null, new()
        {
            ["apiKeySecurityScheme"] = """{"location":"header","name":"X-Key","description":"d"}""",
            ["httpAuthSecurityScheme"] = """{"scheme":"Bearer","bearerFormat":"JWT"}""",
            ["oauth2SecurityScheme"] = """{"flows":{"clientCredentials":{"tokenUrl":"https://id.example/token","scopes":{"read":"Read"}}},"oauth2MetadataUrl":"https://id.example/.well-known/oauth-authorization-server"}""",
            ["openIdConnectSecurityScheme"] = """{"openIdConnectUrl":"https://id.example/.well-known/openid-configuration"}""",
            ["mtlsSecurityScheme"] = """{"description":"client certificate"}""",
        }),
        ["SendMessageResponse"] = (typeof(SendMessageResponse), null, new()
        {
            ["task"] = TaskJson,
            ["message"] = MessageJson,
        }),
        ["StreamResponse"] = (typeof(StreamResponse), null, new()
        {
            ["task"] = TaskJson,
            ["message"] = MessageJson,
            ["statusUpdate"] = """{"taskId":"t1","contextId":"c1","status":{"state":"TASK_STATE_COMPLETED"}}""",
            ["artifactUpdate"] = """{"taskId":"t1","contextId":"c1","artifact":{"artifactId":"a1","parts":[{"text":"x"}]},"append":true,"lastChunk":true}""",
            ["messageUpdate"] = """{"taskId":"t1","contextId":"c1","message":""" + MessageJson + ""","generation":"3"}""",
            ["error"] = """{"taskId":"t1","code":"INVALID_ARGUMENT","message":"bad"}""",
        }),
    };

    private static readonly Dictionary<string, Type> Models = typeof(AgentCard).Assembly.GetTypes()
        .Where(t => t.IsPublic && t.Namespace == "A2A")
        .ToDictionary(t => t.Name, StringComparer.Ordinal);

    private static IEnumerable<MessageDescriptor> Messages() => A2AReflection.Descriptor.MessageTypes;

    private static Type? ModelFor(string protoName) =>
        Models.GetValueOrDefault(Renamed.GetValueOrDefault(protoName, protoName));

    [Fact]
    public void Every_proto_message_has_a_model_type()
    {
        var missing = Messages().Select(m => m.Name).Where(n => ModelFor(n) is null).ToList();
        Assert.True(missing.Count == 0, "No model type for: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_plain_message_has_exactly_the_proto_fields()
    {
        var problems = new List<string>();
        foreach (var message in Messages())
        {
            if (Unions.ContainsKey(message.Name) || ModelFor(message.Name) is not { } type) continue;
            var info = A2AJson.Options.GetTypeInfo(type);
            if (info.Kind != JsonTypeInfoKind.Object)
            {
                problems.Add($"{message.Name}: custom converter; add it to Unions");
                continue;
            }

            var proto = message.Fields.InDeclarationOrder().Select(f => f.JsonName).ToHashSet(StringComparer.Ordinal);
            // [JsonIgnore] computed members are listed with neither a getter nor a setter: not on the wire.
            var model = info.Properties.Where(p => p.Get is not null || p.Set is not null).Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
            var notInModel = proto.Except(model).ToList();
            var notInProto = model.Except(proto).ToList();
            if (notInModel.Count > 0) problems.Add($"{message.Name}: model lacks {string.Join(", ", notInModel)}");
            if (notInProto.Count > 0) problems.Add($"{message.Name}: model has {string.Join(", ", notInProto)}, the proto does not");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_field_of_a_union_message_has_a_sample()
    {
        foreach (var (name, (_, _, samples)) in Unions)
        {
            var message = A2AReflection.Descriptor.FindTypeByName<MessageDescriptor>(name);
            var fields = message.Fields.InDeclarationOrder().Select(f => f.JsonName).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(fields.Order(StringComparer.Ordinal), samples.Keys.Order(StringComparer.Ordinal));
        }
    }

    public static TheoryData<string, string> UnionFields()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, (_, _, samples)) in Unions)
        foreach (var field in samples.Keys)
            data.Add(name, field);
        return data;
    }

    /// <summary>
    ///     Each union case survives a trip through the model: the sample is valid ProtoJSON (the protobuf parser reads it),
    ///     and what the model writes back parses to an equal proto message.
    /// </summary>
    [Theory, MemberData(nameof(UnionFields))]
    public void A_union_field_round_trips_through_the_model(string messageName, string field)
    {
        var (model, @base, samples) = Unions[messageName];
        var descriptor = A2AReflection.Descriptor.FindTypeByName<MessageDescriptor>(messageName);
        var inOneof = descriptor.Fields.InDeclarationOrder().Single(f => f.JsonName == field).ContainingOneof is not null;
        var json = (inOneof || @base is null ? new JsonObject() : JsonNode.Parse(@base)!.AsObject());
        json[field] = JsonNode.Parse(samples[field]);
        var input = json.ToJsonString();

        var expected = JsonParser.Default.Parse(input, descriptor);
        var value = JsonSerializer.Deserialize(input, A2AJson.Options.GetTypeInfo(model));
        var output = JsonSerializer.Serialize(value, A2AJson.Options.GetTypeInfo(model));
        var actual = JsonParser.Default.Parse(output, descriptor);

        Assert.True(expected.Equals(actual), $"{messageName}.{field}: in {input}, out {output}");
    }

    public static TheoryData<string> Enums()
    {
        var data = new TheoryData<string>();
        foreach (var e in A2AReflection.Descriptor.EnumTypes) data.Add(e.Name);
        return data;
    }

    [Theory, MemberData(nameof(Enums))]
    public void Enum_values_match_the_proto(string enumName)
    {
        var descriptor = A2AReflection.Descriptor.EnumTypes.Single(e => e.Name == enumName);
        var type = ModelFor(enumName);
        Assert.NotNull(type);
        var info = A2AJson.Options.GetTypeInfo(type);
        var written = Enum.GetValues(type).Cast<object>()
            .Select(v => JsonSerializer.Serialize(v, info).Trim('"'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(descriptor.Values.Select(v => v.Name).Order(StringComparer.Ordinal), written.Order(StringComparer.Ordinal));
    }
}
