using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using A2A.Client.Cards;

namespace A2A.Cli;

/// <summary>Replaces every known secret value with <c>***</c>. Also matches the JSON-escaped form of a secret.</summary>
internal sealed class SecretRedactor
{
    private readonly List<string> _secrets = [];

    public void Add(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        AddForm(secret);
        AddForm(JsonEncodedText.Encode(secret).ToString());
        AddForm(JsonEncodedText.Encode(secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString());
        // Longest first, so a secret that contains another one is replaced as a whole.
        _secrets.Sort((a, b) => b.Length.CompareTo(a.Length));
    }

    private void AddForm(string form)
    {
        if (!_secrets.Contains(form)) _secrets.Add(form);
    }

    public string Redact(string text)
    {
        foreach (var secret in _secrets) text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text;
    }
}

/// <summary>Cleans error text: no URL query strings (presigned URLs are secrets), no user info in URLs.</summary>
internal static partial class ErrorText
{
    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^(https?://)[^/@\s]*@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPattern();

    public static string StripUrlSecrets(string text) => UrlPattern().Replace(text, m =>
    {
        var url = UserInfoPattern().Replace(m.Value, "$1");
        var cut = url.IndexOfAny(['?', '#']);
        return cut < 0 ? url : url[..cut];
    });
}

/// <summary>
/// The only way text reaches stdout and stderr. It redacts secrets, and its <see cref="One"/> and <see cref="Block"/>
/// methods strip terminal escape and bidi tricks from text that an agent controls.
/// </summary>
internal sealed class Output(CliEnvironment env, bool json)
{
    private readonly SecretRedactor _redactor = new();

    /// <summary>True when <c>--json</c> was given: protocol objects are printed as JSON.</summary>
    public bool Json { get; set; } = json;

    public void AddSecret(string? secret) => _redactor.Add(secret);

    public void Line(string text)
    {
        env.Out.WriteLine(_redactor.Redact(text));
        env.Out.Flush();
    }

    public void ErrorLine(string text)
    {
        env.Error.WriteLine(_redactor.Redact(text));
        env.Error.Flush();
    }

    /// <summary>Agent text as one safe line. Secrets go first, so a cut at the length limit cannot leave half a secret.</summary>
    public string One(string? text, int maxLength = 300) =>
        Block(text, maxLength).Replace('\n', ' ').Replace('\t', ' ');

    /// <summary>Agent text that may span lines, safe to show.</summary>
    public string Block(string? text, int maxLength = 2000) =>
        new UntrustedText(text is null ? null : _redactor.Redact(text), maxLength).ToString();
}
