namespace A2A.Server.TypedSkills;

/// <summary>Card text and the validator of one typed skill.</summary>
/// <typeparam name="TInput">The input type of the skill.</typeparam>
[global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP001", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class TypedSkillOptions<TInput>
{
    internal TypedSkillOptions()
    {
    }

    /// <summary>Human-readable name for the card. Default: the skill id.</summary>
    public string? Name { get; set; }

    /// <summary>What the skill does, for the card. Default: empty.</summary>
    public string? Description { get; set; }

    /// <summary>Keywords for the card.</summary>
    public List<string> Tags { get; } = [];

    /// <summary>Example prompts for the card.</summary>
    public List<string> Examples { get; } = [];

    /// <summary>
    /// Checks rules the type cannot say, such as a range. Return no errors when the input is fine. It runs when the request is
    /// checked and again when the handler starts, so keep it pure and fast. The errors go to the caller: do not put the caller's values in them.
    /// </summary>
    public Func<TInput, IEnumerable<TypedSkillValidationError>>? Validate { get; set; }
}
