
namespace SmBots;

/// <summary>
/// The bot's command-line test aids (<c>--bot-*</c> and the probe flags), read with the client's rules (NetworkView's <c>CmdFlag</c>, <c>CmdArg</c>, <c>CmdArgs</c>,
/// <c>CmdFloat</c>): <c>--name</c> anywhere in the user arguments; a value is the next argument unless it starts with <c>--</c>. The client hands its
/// <c>OS.GetCmdlineUserArgs()</c> to the brain; a server's brain gets <see cref="None"/> (no test aid). The flags are read once at construction: the arguments never change in a process.
/// </summary>
public sealed class BotArgs(IReadOnlyList<string> args)
{
    /// <summary>No arguments: every flag off, every value missing.</summary>
    public static readonly BotArgs None = new([]);

    readonly string[] _args = [.. args];
    /// <summary>The names of every <c>--name</c> argument, taken once at construction (a lookup allocates nothing).</summary>
    readonly HashSet<string> _flags = new(args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).Select(a => a[2..]), StringComparer.Ordinal);

    public IReadOnlyList<string> All => _args;

    /// <summary><c>--name</c> is present.</summary>
    public bool Flag(string name) => _flags.Contains(name);

    /// <summary>The value after <c>--name</c>, or null (absent, last, or followed by another flag).</summary>
    public string? Arg(string name)
    {
        int i = Array.IndexOf(_args, "--" + name);
        return i >= 0 && i + 1 < _args.Length && !_args[i + 1].StartsWith("--") ? _args[i + 1] : null;
    }

    /// <summary>Up to <paramref name="count"/> values after <c>--name</c> (stopping at the next flag); empty when absent.</summary>
    public string[] Args(string name, int count)
    {
        int i = Array.IndexOf(_args, "--" + name);
        if (i < 0) return [];
        return _args.Skip(i + 1).TakeWhile(a => !a.StartsWith("--")).Take(count).ToArray();
    }

    /// <summary>The one value after <c>--name</c> as a finite number (invariant culture), or null (the client's and the brain's <c>FloatArg</c>).</summary>
    public float? FiniteFloat(string name) => FiniteFloat(Args(name, 1));

    /// <summary><paramref name="values"/> (a flag's values) as one finite number (invariant culture), or null.</summary>
    public static float? FiniteFloat(string[] values) =>
        values.Length == 1 && float.TryParse(values[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : null;

    /// <summary>The number after <c>--name</c> (invariant culture), else NaN.</summary>
    public float Float(string name)
    {
        int i = Array.IndexOf(_args, "--" + name);
        return i >= 0 && i + 1 < _args.Length && float.TryParse(_args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
    }
}
