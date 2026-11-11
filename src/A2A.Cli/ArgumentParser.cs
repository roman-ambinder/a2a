using System.Globalization;

namespace A2A.Cli;

/// <summary>One option of a command. A null <paramref name="Metavar" /> means a flag without a value.</summary>
internal sealed record OptionSpec(string Name, char? Short, string? Metavar, string Help, bool Repeatable = false)
{
    public bool TakesValue => Metavar is not null;
}

/// <summary>The options and arguments one command accepts.</summary>
internal sealed record CommandSpec(
    string Name,
    string Summary,
    string Usage,
    int MinPositionals,
    int MaxPositionals,
    IReadOnlyList<OptionSpec> Options,
    Func<CommandContext, CancellationToken, Task<int>> Run,
    bool NeedsInterface = true);

/// <summary>The result of parsing the arguments after the command name.</summary>
internal sealed class ParsedArgs
{
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = [];

    public bool Has(string name)
    {
        return _flags.Contains(name) || _values.ContainsKey(name);
    }

    /// <summary>The value of an option, or null. A value that is empty counts as not given.</summary>
    public string? Value(string name)
    {
        return _values.TryGetValue(name, out var list) && list[^1].Length > 0 ? list[^1] : null;
    }

    public IReadOnlyList<string> Values(string name)
    {
        return _values.TryGetValue(name, out var list) ? list : [];
    }

    public int? Int(string name, int min)
    {
        if (Value(name) is not { } text) return null;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min)
            throw CliException.Usage($"Option '--{name}' needs a whole number of at least {min}.");
        return n;
    }

    public long? Long(string name, long min)
    {
        if (Value(name) is not { } text) return null;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min)
            throw CliException.Usage($"Option '--{name}' needs a whole number of at least {min}.");
        return n;
    }

    public double? Seconds(string name)
    {
        if (Value(name) is not { } text) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !(n > 0) ||
            n > int.MaxValue)
            throw CliException.Usage($"Option '--{name}' needs a number of seconds above 0.");
        return n;
    }

    internal void AddFlag(string name)
    {
        _flags.Add(name);
    }

    internal void AddValue(string name, string value)
    {
        if (!_values.TryGetValue(name, out var list)) _values[name] = list = [];
        list.Add(value);
    }
}

/// <summary>
///     A small parser for <c>--name value</c>, <c>--name=value</c>, short options (<c>-a value</c>) and <c>--</c>.
///     Options may appear anywhere. A lone <c>-</c> is a positional argument.
/// </summary>
internal static class ArgumentParser
{
    /// <summary>Task ids are random base64url, so some start with a dash and look like an option.</summary>
    private const string DashHint = " If it is a value that starts with a dash, such as a task id, put -- before it.";

    public static bool HasHelp(IEnumerable<string> args)
    {
        foreach (var a in args)
        {
            if (a == "--") return false;
            if (a is "--help" or "-h") return true;
        }

        return false;
    }

    public static ParsedArgs Parse(IReadOnlyList<string> args, CommandSpec spec)
    {
        var result = new ParsedArgs();
        var options = spec.Options;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                for (var j = i + 1; j < args.Count; j++) result.Positionals.Add(args[j]);
                break;
            }

            OptionSpec? option;
            string? inline = null;
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=', StringComparison.Ordinal);
                var name = eq < 0 ? arg[2..] : arg[2..eq];
                if (eq >= 0) inline = arg[(eq + 1)..];
                option = options.FirstOrDefault(o => o.Name == name)
                         ?? throw CliException.Usage($"Unknown option '--{name}'.{DashHint}");
            }
            else if (arg.Length > 1 && arg[0] == '-')
            {
                var eq = arg.IndexOf('=', StringComparison.Ordinal);
                var shortName = arg[1..(eq < 0 ? arg.Length : eq)];
                if (eq >= 0) inline = arg[(eq + 1)..];
                option = shortName.Length == 1 ? options.FirstOrDefault(o => o.Short == shortName[0]) : null;
                if (option is null) throw CliException.Usage($"Unknown option '-{shortName}'.{DashHint}");
            }
            else
            {
                result.Positionals.Add(arg);
                continue;
            }

            if (!option.TakesValue)
            {
                if (inline is not null) throw CliException.Usage($"Option '--{option.Name}' does not take a value.");
                result.AddFlag(option.Name);
                continue;
            }

            string value;
            if (inline is not null) value = inline;
            else if (i + 1 < args.Count) value = args[++i];
            else throw CliException.Usage($"Option '--{option.Name}' needs a value.");
            if (!option.Repeatable && result.Has(option.Name))
                throw CliException.Usage($"Option '--{option.Name}' was given more than once.");
            result.AddValue(option.Name, value);
        }

        var count = result.Positionals.Count;
        if (count < spec.MinPositionals)
            throw CliException.Usage($"Missing argument. Usage: a2a {spec.Name} {spec.Usage}".TrimEnd());
        if (count > spec.MaxPositionals)
            throw CliException.Usage($"Too many arguments. Usage: a2a {spec.Name} {spec.Usage}".TrimEnd());
        return result;
    }
}