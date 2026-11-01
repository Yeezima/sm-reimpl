using System.Text.RegularExpressions;

namespace SmContent;

/// <summary>
/// fix/stall-keepalive (ours): which lines of the hosted dedicated server's output the client copies into its own log (as "hosted server: ..."), because the hosted server has no
/// console or file of its own and a player's "timed out" or a caught fault was lost. The server timestamps its operator lines ("[2026-11-01T10:00:00Z] player 2 left from ...");
/// the old filter looked for "player " at the start of the raw line and so never saw a leave.
/// <para>
/// Copied: every join and leave (a leave line carries its reason: left, timed out, idle, banned, dropped after a packet or snapshot fault), every refusal, every fault note
/// ("... failed: ..."), a recipient or packet drop ("dropped after ...") and "FATAL". The server already throttles its fault notes (the first twenty in full, then one every ten
/// seconds, the stack of the first five), so nothing here adds a rate limit; the stack lines that follow a fault note (indented "at ..." lines) are copied too, at most
/// <see cref="MaxTraceLines"/> per note, so a flood of long traces cannot fill the log.
/// </para>
/// </summary>
public sealed class HostedServerLog
{
    /// <summary>Stack lines (and inner-exception markers) copied after one fault note.</summary>
    public const int MaxTraceLines = 14;

    static readonly Regex Stamp = new(@"^\[[0-9TZ:\-\.]+\]\s*", RegexOptions.Compiled);

    readonly object _lock = new();
    int _traceLeft;

    /// <summary>The text to write to the client's log for a line the hosted server printed, or null to leave it out. Safe from the output-reader threads.</summary>
    public string? Filter(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        lock (_lock)
        {
            // A fault's stack: indented lines right after the note.
            if (_traceLeft > 0 && (line.StartsWith(' ') || line.StartsWith('\t')))
            {
                _traceLeft--;
                return line.TrimEnd();
            }
            _traceLeft = 0;
            string body = Stamp.Replace(line, "");
            if (body.StartsWith("note: ", StringComparison.Ordinal)) body = body[6..];
            bool fault = body.Contains(" failed: ", StringComparison.Ordinal) || body.Contains("dropped after", StringComparison.Ordinal)
                || body.StartsWith("FATAL", StringComparison.Ordinal);
            if (fault) { _traceLeft = MaxTraceLines; return line.TrimEnd(); }
            bool player = body.StartsWith("player ", StringComparison.Ordinal)
                && (body.Contains(" joined", StringComparison.Ordinal) || body.Contains(" left", StringComparison.Ordinal));
            return player || body.Contains("refused", StringComparison.OrdinalIgnoreCase) ? line.TrimEnd() : null;
        }
    }
}
