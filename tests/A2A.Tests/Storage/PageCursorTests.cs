using System.Text;
using A2A.Server.Storage;
using A2A.Signing;

namespace A2A.Tests.Storage;

/// <summary>A page token is client input: any malformed token is InvalidParams, never an unexpected exception.</summary>
public sealed class PageCursorTests
{
    private static string Token(string text) => Base64Url.Encode(Encoding.UTF8.GetBytes(text));

    [Theory]
    [InlineData("99999999999999999999|task-1")] // more than long.MaxValue
    [InlineData("-99999999999999999999|task-1")] // less than long.MinValue
    [InlineData("abc|task-1")]
    [InlineData("|task-1")]
    [InlineData("no-separator")]
    public void A_malformed_token_is_InvalidParams(string text)
    {
        var ex = Assert.Throws<A2AException>(() => PageCursor.Decode(Token(text)));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
    }

    [Fact]
    public void A_token_that_is_not_base64url_is_InvalidParams()
    {
        var ex = Assert.Throws<A2AException>(() => PageCursor.Decode("a"));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
    }

    [Fact]
    public void A_valid_token_decodes()
    {
        Assert.Equal((42L, "task-1"), PageCursor.Decode(Token("42|task-1")));
    }
}
