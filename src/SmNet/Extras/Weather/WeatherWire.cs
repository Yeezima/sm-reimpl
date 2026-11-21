using SmSim.Extras.Weather;

namespace SmNet.Extras.Weather;

/// <summary>
/// The weather the server shows now (the <see cref="WeatherWire.StateTag"/> section): the preset, its phase, the milliseconds since it began (the client keeps time from there), whether the developer tools forced it.
/// Classic PvP (W3) adds <paramref name="SpanSeconds"/>, the match length the cycle is fitted to (0: the catalog's own lengths), and <paramref name="Held"/>, the match clock stands still (waiting, countdown, end screen).
/// </summary>
public readonly record struct WeatherStateWire(WeatherId Id, WeatherPhase Phase, uint ElapsedMs, bool Overridden, byte Serial, int SpanSeconds = 0, bool Held = false)
{
    /// <summary>The state a client runs its clock from.</summary>
    public WeatherState ToState() => WeatherClock.At(Id, ElapsedMs / 1000.0, SpanSeconds);
}

/// <summary>Client to server (developer tools only): choose a preset (it starts at its opening phase; None removes the added weather) or Follow Match.</summary>
public enum WeatherCommandKind : byte { Set = 1, Follow = 2 }

public readonly record struct WeatherCommand(WeatherCommandKind Kind, WeatherId Id = WeatherId.None);

/// <summary>
/// The weather wire (weather W1; docs/added-content.md "Weather"): one envelope section and one command, both new tags that only a server running a weather-bearing added content (Warfront, and since W3 the extended Classic
/// profile) sends or reads, so <c>Protocol.Version</c> does not move (the Warfront rules version in the config hash does). Little-endian. Decoders return false on anything malformed and never throw.
/// <list type="bullet">
/// <item>Tag <see cref="StateTag"/>: <c>[u8 preset][u8 phase][u32 elapsed ms][u8 flags: bit 0 overridden][u8 serial]</c> (8 bytes), sent on a change, every <see cref="RepeatSeconds"/> seconds and to a client that joins. The phase must equal the one the preset's clock gives for the elapsed time.
/// W3 (Classic PvP only; Warfront still sends the 8 bytes): <c>... [u16 span seconds]</c> (10 bytes; flags bit 1 = the clock is held): the cycle fitted to a match of that many seconds (<see cref="WeatherClock.At(WeatherId, double, int)"/>); the phase must be the fitted clock's.</item>
/// <item>Command tag <see cref="CommandTag"/> (message kind 17): <c>[u8 kind][u8 preset]</c> (Set) or <c>[u8 kind]</c> (Follow). The server obeys it only under the developer-tools gate (enabled, loopback bind, loopback client).</item>
/// </list>
/// </summary>
public static class WeatherWire
{
    public const byte StateTag = 48, CommandTag = 43;
    public const int StateBytes = 8, FittedStateBytes = 10;
    /// <summary>The server repeats the state this often (seconds): a reconnect or a lost datagram heals within it.</summary>
    public const double RepeatSeconds = 2.0;

    public static byte[] Encode(WeatherStateWire s)
    {
        bool fitted = s.SpanSeconds > 0;
        var b = new byte[fitted ? FittedStateBytes : StateBytes];
        b[0] = (byte)s.Id; b[1] = (byte)s.Phase;
        b[2] = (byte)s.ElapsedMs; b[3] = (byte)(s.ElapsedMs >> 8); b[4] = (byte)(s.ElapsedMs >> 16); b[5] = (byte)(s.ElapsedMs >> 24);
        b[6] = (byte)((s.Overridden ? 1 : 0) | (fitted && s.Held ? 2 : 0));
        b[7] = s.Serial;
        if (fitted) { int span = Math.Clamp(s.SpanSeconds, 1, ushort.MaxValue); b[8] = (byte)span; b[9] = (byte)(span >> 8); }
        return b;
    }

    public static WeatherStateWire Of(WeatherDirector d)
    {
        var st = d.Current;
        uint ms = (uint)Math.Clamp(st.Elapsed * 1000.0, 0, uint.MaxValue);
        int span = d.ShownSpan;
        return new WeatherStateWire(st.Id, WeatherClock.At(st.Id, ms / 1000.0, span).Phase, ms, d.Overridden, d.Serial, span, span > 0 && d.ShownHeld);   // the phase of the millisecond that is sent, so both ends agree to the boundary
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out WeatherStateWire state)
    {
        state = default;
        bool fitted = data.Length == FittedStateBytes;
        if (!fitted && data.Length != StateBytes) return false;
        if (!WeatherCatalog.IsKnown(data[0]) || data[1] > (byte)WeatherPhase.Recovery || (data[6] & ~(fitted ? 3 : 1)) != 0) return false;
        int span = fitted ? data[8] | data[9] << 8 : 0;
        if (fitted && span == 0) return false;
        uint ms = (uint)(data[2] | data[3] << 8 | data[4] << 16 | data[5] << 24);
        var id = (WeatherId)data[0];
        if (WeatherClock.At(id, ms / 1000.0, span).Phase != (WeatherPhase)data[1]) return false;
        state = new WeatherStateWire(id, (WeatherPhase)data[1], ms, (data[6] & 1) != 0, data[7], span, (data[6] & 2) != 0);
        return true;
    }

    public static byte[] Encode(WeatherCommand c) => c.Kind == WeatherCommandKind.Set ? [(byte)c.Kind, (byte)c.Id] : [(byte)c.Kind];

    public static bool TryDecode(ReadOnlySpan<byte> data, out WeatherCommand command)
    {
        command = default;
        if (data.Length == 2 && data[0] == (byte)WeatherCommandKind.Set && WeatherCatalog.IsKnown(data[1])) { command = new WeatherCommand(WeatherCommandKind.Set, (WeatherId)data[1]); return true; }
        if (data.Length == 1 && data[0] == (byte)WeatherCommandKind.Follow) { command = new WeatherCommand(WeatherCommandKind.Follow); return true; }
        return false;
    }
}
