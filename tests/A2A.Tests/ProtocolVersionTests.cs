namespace A2A.Tests;

/// <summary><see cref="A2AProtocol.IsVersion11OrLater"/> parses in place; it must answer as the split-based form did.</summary>
public sealed class ProtocolVersionTests
{
    private static bool Reference(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var parts = version!.Trim().Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minor))
            return false;
        return major == 1 && minor >= 1;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData("1.0")]
    [InlineData("1.1")]
    [InlineData(" 1.1 ")]
    [InlineData("1.10")]
    [InlineData("1.1.0")]
    [InlineData("1.1.x")]
    [InlineData("2.0")]
    [InlineData("0.3")]
    [InlineData("01.01")]
    [InlineData("+1.1")]
    [InlineData("1.-1")]
    [InlineData("1. 1")]
    [InlineData("1 .1")]
    [InlineData("1.2147483647")]
    [InlineData("1.2147483648")]
    [InlineData("1.99999999999")]
    [InlineData("١.١")] // Arabic-Indic digits are not ASCII digits
    [InlineData("1.1 ")]
    public void Answers_as_the_split_based_parse(string? version) =>
        Assert.Equal(Reference(version), A2AProtocol.IsVersion11OrLater(version));
}
