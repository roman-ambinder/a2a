using System.Text.Json.Serialization;

namespace A2A.Tests.TypedSkills;

public sealed class BookingRequest
{
    public required string City { get; init; }
    public int Nights { get; init; }
    public List<string>? Extras { get; init; }
    public Dictionary<string, int>? Counts { get; init; }
}

public sealed class BookingResult
{
    public required string City { get; init; }
    public int Nights { get; init; }
    public required string Reference { get; init; }
}

/// <summary>A recursive type: its schema refers to itself.</summary>
public sealed class TreeNode
{
    public required string Name { get; init; }
    public List<TreeNode>? Children { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BookingRequest))]
[JsonSerializable(typeof(BookingResult))]
[JsonSerializable(typeof(TreeNode))]
internal sealed partial class TypedJson : JsonSerializerContext
{
}
