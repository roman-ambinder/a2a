namespace A2A.Server.TypedSkills;

/// <summary>One problem found by a <see cref="TypedSkillOptions{TInput}.Validate"/> delegate. It is sent to the caller.</summary>
[global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP001", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class TypedSkillValidationError
{
    /// <summary>Creates an error. Write both texts yourself: never copy the value the caller sent.</summary>
    /// <param name="field">The field, as a JSON path into the input, for example <c>$.nights</c>.</param>
    /// <param name="description">What is wrong, for example "Must be between 1 and 30."</param>
    public TypedSkillValidationError(string field, string description)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(description);
        Field = field;
        Description = description;
    }

    /// <summary>The field, as a JSON path into the input.</summary>
    public string Field { get; }

    /// <summary>What is wrong with the field.</summary>
    public string Description { get; }
}
