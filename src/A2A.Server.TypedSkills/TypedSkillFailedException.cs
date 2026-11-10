using System.Diagnostics.CodeAnalysis;

namespace A2A.Server.TypedSkills;

/// <summary>
///     Throw it from a typed skill handler to fail the task with a message the caller may see. Any other exception follows
///     the normal
///     engine path, and the caller gets generic text.
/// </summary>
[Experimental("A2AEXP001",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class TypedSkillFailedException : Exception
{
    /// <summary>
    ///     Creates the exception. <paramref name="message" /> becomes the status message of the failed task: it reaches
    ///     the caller as it is.
    /// </summary>
    public TypedSkillFailedException(string message) : base(message)
    {
    }
}