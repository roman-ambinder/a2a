using System.Text;

namespace A2A.Elicitations;

/// <summary>Finds field names, titles and descriptions that ask for a secret. A guard against mistakes, not a complete filter.</summary>
internal static class ElicitationSensitivity
{
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "password", "passwd", "pwd", "passphrase", "passcode", "secret", "credential", "credentials", "token", "apikey",
        "privatekey", "cvv", "cvc", "ssn", "pin", "otp", "iban",
    };

    private static readonly HashSet<string> Pairs = new(StringComparer.Ordinal)
    {
        "api key", "private key", "access key", "card number", "credit card", "debit card", "security code", "social security",
    };

    public static bool Looks(string name, string? title, string? description) =>
        LooksIn(name) || (title is not null && LooksIn(title)) || (description is not null && LooksIn(description));

    private static bool LooksIn(string text)
    {
        var tokens = Tokens(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (Words.Contains(tokens[i])) return true;
            if (i + 1 < tokens.Count && Pairs.Contains(tokens[i] + " " + tokens[i + 1])) return true;
        }
        return false;
    }

    /// <summary>Lowercase words, split on case changes (<c>apiKey</c>, <c>APIKey</c>) and on anything that is not a letter or digit.</summary>
    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush(tokens, word);
                continue;
            }
            if (word.Length > 0 && char.IsUpper(c))
            {
                var previous = text[i - 1];
                var acronymEnds = char.IsUpper(previous) && i + 1 < text.Length && char.IsLower(text[i + 1]);
                if (char.IsLower(previous) || char.IsDigit(previous) || acronymEnds) Flush(tokens, word);
            }
            word.Append(char.ToLowerInvariant(c));
        }
        Flush(tokens, word);
        return tokens;
    }

    private static void Flush(List<string> tokens, StringBuilder word)
    {
        if (word.Length == 0) return;
        tokens.Add(word.ToString());
        word.Clear();
    }
}
