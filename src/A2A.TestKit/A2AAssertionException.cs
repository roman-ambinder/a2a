namespace A2A.TestKit;

/// <summary>Thrown when an assertion or a wait of the test kit fails. Any test framework reports it as a failed test.</summary>
public sealed class A2AAssertionException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public A2AAssertionException() : base("An A2A assertion failed.")
    {
    }

    /// <summary>Creates the exception with <paramref name="message" />.</summary>
    public A2AAssertionException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message" /> and the exception that caused it.</summary>
    public A2AAssertionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}