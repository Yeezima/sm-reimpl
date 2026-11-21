using System.Text;
using SmNet.Extras.Warfront.Results;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;

namespace SmNet.Extras.Warfront;

/// <summary>Where a stage change stands (the <c>Stage</c> section): None = no stage change, the status text then carries the last deploy refusal; Preparing = the server builds the target's maps while everyone is still connected; Go = rejoin the named map; Arrived = this process is the target's, landing under way.</summary>
public enum WarfrontStageState : byte { None = 0, Preparing = 1, Go = 2, Arrived = 3 }

/// <summary>The <c>Hub</c> section: the fields of the client's <c>WarfrontHubView</c> (that type lives in the client project; W5b builds it from this record). Keys in <see cref="Players"/> and <see cref="LeaderKey"/> are handles: the viewer's own key as it is, every other player's a one-way pseudonym (<see cref="WarfrontWire.Pseudonym"/>), so no client learns another's campaign key.</summary>
public sealed record WarfrontHubWire(
    WarfrontPhase Phase, IReadOnlyList<int> Fronts, IReadOnlyList<bool> Advanced, bool HqMandatory, int Deployments,
    IReadOnlyList<WarfrontTarget> Targets, WarfrontTarget? CurrentTarget, ulong ViewerKey, ulong? LeaderKey,
    IReadOnlyList<WarfrontHubWirePlayer> Players, IReadOnlyList<byte> Last, IReadOnlyList<byte>? Forecast = null);

/// <summary>One player of the run in join order; <see cref="Key"/> is a handle (see <see cref="WarfrontHubWire"/>).</summary>
public readonly record struct WarfrontHubWirePlayer(ulong Key, string Name, bool Connected);

/// <summary>The <c>Stage</c> section. <see cref="Percent"/> 0..100 of the preparation, <see cref="ElapsedSeconds"/> since it began, <see cref="Arrived"/> of <see cref="Expected"/> players back on the new map.</summary>
public sealed record WarfrontStageWire(WarfrontStageState State, WarfrontTargetKind Kind, int Lane, string Map, int Percent, int ElapsedSeconds, int Arrived, int Expected, string Status);

/// <summary>The <c>Down</c> section: the viewer's own fallen state and the seconds left of it (tenths on the wire).</summary>
public sealed record WarfrontDownWire(WarfrontDownPhase Phase, float SecondsLeft);

/// <summary>Client to server commands of Warfront (message kind 17 under <see cref="WarfrontWire.CommandTag"/>).</summary>
public enum WarfrontClientCommandKind : byte { Identify = 1, Deploy = 2, Pick = 3, NewRun = 4 }

/// <summary>One decoded command. <see cref="Key"/> and <see cref="Name"/> only for Identify; <see cref="Target"/> only for Deploy; <see cref="Slot"/> and <see cref="Pick"/> only for Pick.</summary>
public sealed record WarfrontClientCommand(WarfrontClientCommandKind Kind, ulong Key = 0, string Name = "", WarfrontTarget Target = default, int Slot = 0, int Pick = 0);

/// <summary>
/// Warfront's wire (slice W5a, the shared contract W5b reads; no change to <c>Protocol.Version</c> or <c>ContentExtras.WireVersion</c>: it is new envelope tags and one new command tag that only a Warfront
/// server sends or reads). Values are little-endian. Every decoder is a <c>TryDecode</c> that returns false on any malformed, truncated, oversized or out-of-range payload and never throws.
/// <list type="bullet">
/// <item>Tag <see cref="HubTag"/> (per client): <c>[u8 phase][u8 f0][u8 f1][u8 f2][u8 advanced bits][u8 flags: bit 0 HQ mandatory, bit 1 current target, bit 2 leader][u16 deployments]</c>
/// <c>[u8 nTargets]{[u8 kind][i8 lane]}[u8 kind][i8 lane] if current target[u64 viewer handle][u64 leader handle] if leader[u8 nPlayers]{[u64 handle][u8 connected][u8 len][name]}[u8 last0][u8 last1][u8 last2]</c>, then (weather W1, WarfrontRules.Version 18) <c>[u8 nForecast: 0 or 9]{[u8 weather id]}</c>, the forecast of lane slots 1 to 9 (a payload that ends before it decodes with no forecast).</item>
/// <item>Tag <see cref="StageTag"/>: <c>[u8 state][u8 kind][i8 lane][u8 percent][u16 elapsed s][u8 arrived][u8 expected][u8 len][map][u8 len][status]</c>; sent on a change, every 2 s while Preparing, and to a client that joins.</item>
/// <item>Tag <see cref="DownTag"/> (per client): <c>[u8 phase][u16 tenths of a second]</c>.</item>
/// <item>Tag <see cref="ResultsTag"/>: one chunk of <see cref="WarfrontResultsWire.Split"/> (<c>[u8 transfer][u8 index][u8 count]</c> then record bytes); no key is in the record.</item>
/// </list>
/// Commands (kind 17, tag <see cref="CommandTag"/>, at most <see cref="Protocol.MaxExtrasCommandPayload"/> bytes): <c>[u8 kind]</c> then Identify <c>[u64 key][u8 len][name]</c>; Deploy <c>[u8 target kind][i8 lane]</c>;
/// Pick <c>[u8 slot][u8 pick index]</c>; NewRun nothing.
/// </summary>
public static class WarfrontWire
{
    /// <summary>The Warfront wire's own number; raised with <see cref="SmSim.Extras.Warfront.WarfrontRules.Version"/>.</summary>
    public const int Format = 1;
    /// <summary>
    /// Envelope tags (gore 1, barks 2, Musou 3 to 18). Reserved for later slices: 32 to 34 the squad wire (W10), 44 and 45 progression (W8), 46 coop (W12); 35 to 39 and 47 up stay free.
    /// </summary>
    public const byte HubTag = 40, StageTag = 41, DownTag = 42, ResultsTag = 43;
    /// <summary>The command tag (its own namespace, client to server: Musou's is 7, the Librarian's 16). 41 is the squad's (Rename, W10), 42 the coop's (ConfirmClass, Watch; W12): both admitted by the server and passed to those slices.</summary>
    public const byte CommandTag = 40, SquadCommandTag = 41, CoopCommandTag = 42;
    public const int MaxName = 24, MaxMap = 32, MaxStatus = 48, MaxPlayers = 16, MaxTargets = 8, Lanes = 3;
    public const int MaxLane = 2;

    // ---- handles -------------------------------------------------------------------------------------------------------------------------

    /// <summary>The handle another client sees for <paramref name="key"/>: a one-way mix of the key and the run seed, stable for the run, never the key itself.</summary>
    public static ulong Pseudonym(ulong key, ulong runSeed)
    {
        ulong x = key ^ 0xD6E8FEB86659FD93UL;
        x = Mix(x + runSeed * 0x9E3779B97F4A7C15UL);
        x = Mix(x ^ 0xA0761D6478BD642FUL);
        return x == 0 ? 1 : x;
    }

    static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>The Hub of one campaign for the viewer <paramref name="viewerKey"/>: the viewer's own handle is the key, the others' pseudonyms; last-deployment kinds as <c>WarfrontHubView.FromCampaign</c> works them out (0 none, 1 won, 2 wiped, 3 wiped held at the floor, 4 roll won, 5 roll lost, 6 roll lost held).</summary>
    public static WarfrontHubWire HubFor(WarfrontCampaign c, ulong viewerKey)
    {
        var fronts = new int[Lanes]; var advanced = new bool[Lanes]; var last = new byte[Lanes];
        for (int l = 0; l < Lanes; l++) { fronts[l] = c.Front(l); advanced[l] = c.Advanced(l); }
        if (c.Deployments > 0)
        {
            var entry = c.History.LastOrDefault(h => h.Deployment == c.Deployments);
            if (entry is not null && entry.Target.Lane >= 0)
                last[entry.Target.Lane] = (byte)(entry.Outcome == WarfrontOutcome.Win ? 1 : entry.HeldAtFloor ? 3 : 2);
            foreach (var r in c.Rolls)
                if (r.Deployment == c.Deployments) last[r.Lane] = (byte)(!r.Lost ? 4 : r.HeldAtFloor ? 6 : 5);
        }
        ulong Handle(ulong k) => k == viewerKey ? k : Pseudonym(k, c.RunSeed);
        var players = new List<WarfrontHubWirePlayer>();
        foreach (var k in c.JoinOrder) players.Add(new WarfrontHubWirePlayer(Handle(k), c.Player(k)?.Name ?? "", c.IsConnected(k)));
        if (players.Count > MaxPlayers) players.RemoveRange(MaxPlayers, players.Count - MaxPlayers);
        return new WarfrontHubWire(c.Phase, fronts, advanced, c.HqMandatory, c.Deployments, c.AvailableTargets().ToArray(), c.CurrentTarget,
            viewerKey, c.Leader is { } l0 ? Handle(l0) : null, players, last, c.Forecast.ToArray());
    }

    // ---- encoders ------------------------------------------------------------------------------------------------------------------------

    public static byte[] Encode(WarfrontHubWire h)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)h.Phase);
        for (int l = 0; l < Lanes; l++) w.Write((byte)(l < h.Fronts.Count ? h.Fronts[l] : 1));
        byte adv = 0;
        for (int l = 0; l < Lanes && l < h.Advanced.Count; l++) if (h.Advanced[l]) adv |= (byte)(1 << l);
        w.Write(adv);
        w.Write((byte)((h.HqMandatory ? 1 : 0) | (h.CurrentTarget is not null ? 2 : 0) | (h.LeaderKey is not null ? 4 : 0)));
        w.Write((ushort)Math.Clamp(h.Deployments, 0, ushort.MaxValue));
        var targets = h.Targets.Take(MaxTargets).ToArray();
        w.Write((byte)targets.Length);
        foreach (var t in targets) { w.Write((byte)t.Kind); w.Write((sbyte)t.Lane); }
        if (h.CurrentTarget is { } c) { w.Write((byte)c.Kind); w.Write((sbyte)c.Lane); }
        w.Write(h.ViewerKey);
        if (h.LeaderKey is { } leader) w.Write(leader);
        var players = h.Players.Take(MaxPlayers).ToArray();
        w.Write((byte)players.Length);
        foreach (var p in players) { w.Write(p.Key); w.Write(p.Connected); Str(w, p.Name, MaxName); }
        for (int l = 0; l < Lanes; l++) w.Write(l < h.Last.Count ? h.Last[l] : (byte)0);
        bool forecast = h.Forecast is { Count: SmSim.Extras.Warfront.Weather.WarfrontForecast.Slots };
        w.Write((byte)(forecast ? SmSim.Extras.Warfront.Weather.WarfrontForecast.Slots : 0));
        if (forecast) foreach (var id in h.Forecast!) w.Write(id);
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] Encode(WarfrontStageWire s)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)s.State); w.Write((byte)s.Kind); w.Write((sbyte)s.Lane); w.Write((byte)Math.Clamp(s.Percent, 0, 100));
        w.Write((ushort)Math.Clamp(s.ElapsedSeconds, 0, ushort.MaxValue)); w.Write((byte)Math.Clamp(s.Arrived, 0, 255)); w.Write((byte)Math.Clamp(s.Expected, 0, 255));
        Str(w, s.Map, MaxMap); Str(w, s.Status, MaxStatus);
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] Encode(WarfrontDownWire d)
    {
        var b = new byte[3];
        b[0] = (byte)d.Phase;
        ushort tenths = (ushort)Math.Clamp((int)MathF.Round(float.IsFinite(d.SecondsLeft) ? d.SecondsLeft * 10f : 0f), 0, ushort.MaxValue);
        b[1] = (byte)tenths; b[2] = (byte)(tenths >> 8);
        return b;
    }

    /// <summary>The Results chunks of an encoded record, each as an envelope section payload under <see cref="ResultsTag"/>.</summary>
    public static IReadOnlyList<byte[]> ResultsChunks(byte[] encodedRecord, byte transferId) => WarfrontResultsWire.Split(encodedRecord, transferId);

    public static byte[] Encode(WarfrontClientCommand c)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)c.Kind);
        switch (c.Kind)
        {
            case WarfrontClientCommandKind.Identify: w.Write(c.Key); Str(w, c.Name, MaxName); break;
            case WarfrontClientCommandKind.Deploy: w.Write((byte)c.Target.Kind); w.Write((sbyte)c.Target.Lane); break;
            case WarfrontClientCommandKind.Pick: w.Write((byte)c.Slot); w.Write((byte)c.Pick); break;
        }
        w.Flush();
        var bytes = ms.ToArray();
        if (bytes.Length > Protocol.MaxExtrasCommandPayload) throw new ArgumentException("command too large");
        return bytes;
    }

    // ---- decoders: false on anything malformed, never an exception --------------------------------------------------------------------

    public static bool TryDecode(ReadOnlySpan<byte> data, out WarfrontHubWire hub)
    {
        hub = null!;
        try
        {
            var r = new Rd(data);
            int phase = r.U8();
            if (phase > (int)WarfrontPhase.Lost) return false;
            var fronts = new int[Lanes];
            for (int l = 0; l < Lanes; l++) { fronts[l] = r.U8(); if (fronts[l] > 4) return false; }
            int adv = r.U8();
            if ((adv & ~7) != 0) return false;
            int flags = r.U8();
            if ((flags & ~7) != 0) return false;
            int deployments = r.U16();
            int nt = r.U8();
            if (nt > MaxTargets) return false;
            var targets = new List<WarfrontTarget>(nt);
            for (int i = 0; i < nt; i++) { if (!r.Target(out var t)) return false; targets.Add(t); }
            WarfrontTarget? current = null;
            if ((flags & 2) != 0) { if (!r.Target(out var t)) return false; current = t; }
            ulong viewer = r.U64();
            ulong? leader = (flags & 4) != 0 ? r.U64() : null;
            int np = r.U8();
            if (np > MaxPlayers) return false;
            var players = new List<WarfrontHubWirePlayer>(np);
            for (int i = 0; i < np; i++)
            {
                ulong key = r.U64(); int conn = r.U8();
                if (conn > 1) return false;
                if (!r.Str(MaxName, out var name)) return false;
                players.Add(new WarfrontHubWirePlayer(key, name, conn == 1));
            }
            var last = new byte[Lanes];
            for (int l = 0; l < Lanes; l++) { last[l] = (byte)r.U8(); if (last[l] > 6) return false; }
            byte[]? forecast = null;
            if (!r.End)
            {
                int nf = r.U8();
                if (nf != 0 && nf != SmSim.Extras.Warfront.Weather.WarfrontForecast.Slots) return false;
                if (nf != 0)
                {
                    forecast = new byte[nf];
                    for (int i = 0; i < nf; i++) { forecast[i] = (byte)r.U8(); if (!SmSim.Extras.Weather.WeatherCatalog.IsKnown(forecast[i])) return false; }
                }
                if (!r.End) return false;
            }
            hub = new WarfrontHubWire((WarfrontPhase)phase, fronts, new[] { (adv & 1) != 0, (adv & 2) != 0, (adv & 4) != 0 }, (flags & 1) != 0, deployments, targets, current, viewer, leader, players, last, forecast);
            return true;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or EndOfStreamException) { return false; }
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out WarfrontStageWire stage)
    {
        stage = null!;
        try
        {
            var r = new Rd(data);
            int state = r.U8(), kind = r.U8(), lane = (sbyte)r.U8(), percent = r.U8(), elapsed = r.U16(), arrived = r.U8(), expected = r.U8();
            if (state < 0 || state > (int)WarfrontStageState.Arrived || kind > (int)WarfrontTargetKind.Fortress || lane < -1 || lane > MaxLane || percent > 100) return false;
            if (!r.Str(MaxMap, out var map) || !r.Str(MaxStatus, out var status) || !r.End) return false;
            stage = new WarfrontStageWire((WarfrontStageState)state, (WarfrontTargetKind)kind, lane, map, percent, elapsed, arrived, expected, status);
            return true;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { return false; }
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out WarfrontDownWire down)
    {
        down = null!;
        if (data.Length != 3 || data[0] > (byte)WarfrontDownPhase.Spectator) return false;
        down = new WarfrontDownWire((WarfrontDownPhase)data[0], (data[1] | data[2] << 8) / 10f);
        return true;
    }

    /// <summary>A command's bytes; false (never a throw) for an unknown kind, a bad lane or kind, an empty key or name overrun, or trailing bytes.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out WarfrontClientCommand command)
    {
        command = null!;
        try
        {
            if (data.Length is 0 or > Protocol.MaxExtrasCommandPayload) return false;
            var r = new Rd(data);
            int kind = r.U8();
            switch ((WarfrontClientCommandKind)kind)
            {
                case WarfrontClientCommandKind.Identify:
                {
                    ulong key = r.U64();
                    if (key == 0 || !r.Str(MaxName, out var name) || !r.End) return false;
                    command = new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: key, Name: name);
                    return true;
                }
                case WarfrontClientCommandKind.Deploy:
                    if (!r.Target(out var t) || !r.End) return false;
                    command = new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: t);
                    return true;
                case WarfrontClientCommandKind.Pick:
                {
                    int slot = r.U8(), pick = r.U8();
                    if (!r.End) return false;
                    command = new WarfrontClientCommand(WarfrontClientCommandKind.Pick, Slot: slot, Pick: pick);
                    return true;
                }
                case WarfrontClientCommandKind.NewRun:
                    if (!r.End) return false;
                    command = new WarfrontClientCommand(WarfrontClientCommandKind.NewRun);
                    return true;
                default: return false;
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException) { return false; }
    }

    // ---- bytes ---------------------------------------------------------------------------------------------------------------------------

    static void Str(BinaryWriter w, string s, int max)
    {
        var b = Encoding.UTF8.GetBytes(s ?? "");
        if (b.Length > max) b = b[..max];   // a cut inside a character decodes as a replacement character, never an error
        w.Write((byte)b.Length); w.Write(b);
    }

    /// <summary>A bounds-checked reader: a read past the end throws IndexOutOfRange (caught by every decoder).</summary>
    ref struct Rd
    {
        readonly ReadOnlySpan<byte> _d; int _at;
        public Rd(ReadOnlySpan<byte> d) { _d = d; _at = 0; }
        public readonly bool End => _at == _d.Length;
        public int U8() => _d[_at++];
        public int U16() { int v = _d[_at] | _d[_at + 1] << 8; _at += 2; return v; }
        public ulong U64()
        {
            ulong v = 0;
            for (int i = 7; i >= 0; i--) v = v << 8 | _d[_at + i];
            _at += 8; return v;
        }
        public bool Str(int max, out string s)
        {
            s = "";
            int n = U8();
            if (n > max || _at + n > _d.Length) return false;
            s = Encoding.UTF8.GetString(_d.Slice(_at, n)); _at += n;
            return true;
        }
        public bool Target(out WarfrontTarget t)
        {
            int kind = U8(), lane = (sbyte)U8();
            t = default;
            if (kind > (int)WarfrontTargetKind.Fortress || lane < -1 || lane > MaxLane) return false;
            if ((WarfrontTargetKind)kind == WarfrontTargetKind.Hq != (lane == -1)) return false;   // the HQ has no lane, a node and the fortress have one
            t = new WarfrontTarget((WarfrontTargetKind)kind, lane);
            return true;
        }
    }
}
