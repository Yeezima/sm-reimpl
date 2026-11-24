using System.Collections.Concurrent;

namespace SmBots;

/// <summary>
/// The bot's command-line test aids (<c>--bot-*</c> and the probe flags), read with the client's rules (NetworkView's <c>CmdFlag</c>, <c>CmdArg</c>, <c>CmdArgs</c>,
/// <c>CmdFloat</c>): <c>--name</c> anywhere in the user arguments; a value is the next argument unless it starts with <c>--</c>. The client hands its
/// <c>OS.GetCmdlineUserArgs()</c> to the brain; a server's brain gets <see cref="None"/> (no test aid). Lookups are cached: the arguments never change in a process.
/// </summary>
public sealed class BotArgs(IReadOnlyList<string> args)
{
    /// <summary>No arguments: every flag off, every value missing.</summary>
    public static readonly BotArgs None = new([]);

    readonly string[] _args = [.. args];
    readonly ConcurrentDictionary<string, bool> _flags = new(StringComparer.Ordinal);

    public IReadOnlyList<string> All => _args;

    /// <summary><c>--name</c> is present.</summary>
    public bool Flag(string name) => _flags.GetOrAdd(name, n => Array.IndexOf(_args, "--" + n) >= 0);

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

    /// <summary>The number after <c>--name</c> (invariant culture), else NaN.</summary>
    public float Float(string name)
    {
        int i = Array.IndexOf(_args, "--" + name);
        return i >= 0 && i + 1 < _args.Length && float.TryParse(_args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
    }
}
