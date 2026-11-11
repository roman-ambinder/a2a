using A2A.Client.Cards;

namespace A2A.Tests;

/// <summary>Card text escaped for the place it is shown: HTML pages and Markdown chat output (S6).</summary>
public class UntrustedTextTests
{
    [Fact]
    public void ToHtml_encodes_markup_so_card_text_cannot_inject_html()
    {
        var text = new UntrustedText("<img src=x onerror=\"alert(1)\"> & 'quotes'");
        Assert.Equal("&lt;img src=x onerror=&quot;alert(1)&quot;&gt; &amp; &#39;quotes&#39;", text.ToHtml());
    }

    [Fact]
    public void ToMarkdown_escapes_punctuation_so_card_text_cannot_add_links_or_formatting()
    {
        var text = new UntrustedText("[click here](https://evil.example) **now** `code` <b>");
        Assert.Equal(@"\[click here\]\(https\:\/\/evil\.example\) \*\*now\*\* \`code\` \<b\>", text.ToMarkdown());
    }

    [Fact]
    public void Both_escape_the_sanitized_text_not_the_raw_text()
    {
        // The bidi override and the zero-width space are removed first, as ToString removes them.
        var text = new UntrustedText("a‮b​c<");
        Assert.Equal("abc<", text.ToString());
        Assert.Equal("abc&lt;", text.ToHtml());
        Assert.Equal(@"abc\<", text.ToMarkdown());
    }

    [Fact]
    public void Plain_letters_digits_and_spaces_are_unchanged()
    {
        var text = new UntrustedText("Weather agent 2 for Oslo");
        Assert.Equal("Weather agent 2 for Oslo", text.ToHtml());
        Assert.Equal("Weather agent 2 for Oslo", text.ToMarkdown());
    }
}
