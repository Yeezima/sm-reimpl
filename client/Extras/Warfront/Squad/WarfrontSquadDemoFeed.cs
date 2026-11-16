using System;
using System.Collections.Generic;
using System.Numerics;
using SmNet.Extras.Warfront;

namespace SmClient.Extras.Warfront.Squad;

/// <summary>
/// Test aid (W10): a scripted squad that goes through the wire's own writers (<see cref="SquadWire"/>), so a windowed run shows the whole client path without a server: up to four squads of up to nine Marines
/// (36), owner players 1 to 4 (the viewer is player 1), square formations. Squad 0 (the viewer's) walks six seconds and stands six firing at the Orks ahead; squad 1 walks a circle; squad 2 stands and fires;
/// squad 3 (a Librarian's: the class's own armour in the owner's colours) hops (a leap every five seconds). Every 24 s one Marine of squad 0 and one of squad 2 (both in the back row) are downed, revived by the viewer
/// (share 0 to 1 over two seconds) after eight. **Placeholder demonstration**; the tests of the wire and the model are in the xUnit suite. Deterministic; positions in game space, metres.
/// </summary>
public sealed class WarfrontSquadDemoFeed
{
    public const int OwnerCount = 4, TicksPerSecond = 10;
    public const double Cycle = 24.0, DownAt = 2.0, ReviveFrom = 8.0, ReviveSeconds = 2.0;
    const float Spacing = 1.7f;

    readonly int _per, _owners;
    readonly float _spread;
    readonly Vector3 _centre;
    readonly Random _rng = new(20261117);
    double _clock, _next;
    uint _phase;
    int _rosterRound;
    readonly bool[] _down;
    readonly List<SquadEventWire> _events = new();

    public static readonly string[] Names =
    [
        "Cassian", "Titus", "Lysander", "Octavian", "Marius", "Severus", "Decimus", "Valerian", "Augustin", "Tiberius", "Corvin", "Leandros", "Gaius", "Aurelian", "Brutus", "Cato", "Draven", "Eumenes",
        "Fabian", "Gallus", "Hadrian", "Ignatius", "Julian", "Kasimir", "Livius", "Maximus", "Nestor", "Orion", "Pelias", "Quintus", "Remus", "Sabinus", "Thaddeus", "Ulric", "Varro", "Xander",
    ];

    /// <summary>The class ids of the four squads (0 Assault, 1 Tactical, 2 Devastator; the viewer's is the Tactical one) and whether each wears its owner's own pieces.</summary>
    public static readonly byte[] SquadClass = [1, 0, 2, 1];
    public static readonly bool[] OwnPieces = [true, true, true, false];

    public int PerOwner => _per;
    public int Owners => _owners;
    public int Count => _per * _owners;
    /// <summary>The player id that owns squad <paramref name="owner"/>.</summary>
    public static int PlayerOf(int owner) => owner + 1;
    public static ulong HandleOf(int owner) => 0xC0FFEE00UL + (ulong)owner;
    public static string PlayerName(int player) => player switch { 1 => "You", 2 => "Brann", 3 => "Cato", _ => "Dax" };

    /// <param name="spread">Scales the distances between the squads (1: the full arena; 0.5 for a closer picture).</param>
    public WarfrontSquadDemoFeed(int perOwner = 9, int owners = OwnerCount, Vector3 centre = default, float spread = 1f)
    {
        _spread = spread;
        _per = Math.Clamp(perOwner, 1, SquadWire.MaxRosterMarines);
        _owners = Math.Clamp(owners, 1, OwnerCount);
        _centre = centre;
        _down = new bool[SquadWire.MaxMarines];
    }

    /// <summary>The Orks' places: ids 1001 to 1012 stand in a line 36 m north of the viewer's squad (game space).</summary>
    public Vector3? EnemyAt(int id) => id is >= 1001 and <= 1012 ? _centre + new Vector3((id - 1006.5f) * 3f, 38f + (id % 3) * 1.5f, 0f) : null;

    static float WalkY(double t)
    {
        double q = t % 24.0;
        return (float)(q < 6.0 ? 2.0 * q : q < 12.0 ? 12.0 : q < 18.0 ? 12.0 - 2.0 * (q - 12.0) : 0.0);
    }

    Vector3 SquadCentre(int owner, double t) => owner switch
    {
        0 => _centre + new Vector3(0f, WalkY(t), 0f),
        1 => _centre + new Vector3(-30f * _spread + 8f * MathF.Cos((float)t * 0.3f), -6f * _spread + 8f * MathF.Sin((float)t * 0.3f), 0f),
        2 => _centre + new Vector3(30f * _spread, -4f * _spread, 0f),
        _ => _centre + new Vector3(-6f * _spread, -34f * _spread, 0f),
    };

    static bool Moving(int owner, double t) => owner switch { 0 => t % 24.0 < 6.0 || (t % 24.0 >= 12.0 && t % 24.0 < 18.0), 1 => true, _ => false };

    (Vector3 Position, float Yaw, bool Firing, bool Airborne, bool Moving) Place(int owner, int slot, double t)
    {
        int col = slot % 3 - 1, row = slot / 3;
        var c = SquadCentre(owner, t);
        bool moving = Moving(owner, t);
        float yaw = MathF.PI / 2f;   // facing north, the Orks
        if (owner == 1) { float a = (float)t * 0.3f; yaw = MathF.Atan2(MathF.Cos(a), -MathF.Sin(a)); }
        else if (owner == 0 && moving && t % 24.0 >= 12.0) yaw = -MathF.PI / 2f;
        var p = c + new Vector3(col * Spacing, -row * Spacing, 0f);
        bool airborne = false;
        if (owner == 3 && slot < 3)
        {
            double k = (t + slot * 0.4) % 5.0;
            if (k < 1.2) { airborne = true; p.Z += 3.2f * MathF.Sin((float)(k / 1.2) * MathF.PI); p.Y += (float)(k * 2.0); }
        }
        bool firing = !moving && owner is 0 or 2 && !airborne;
        return (p, yaw, firing, airborne, moving);
    }

    /// <summary>The slot of squad <paramref name="owner"/> that is downed in the cycle (squad 0's eighth, in the back row, and squad 2's ninth), or -1.</summary>
    static int DownSlot(int owner) => owner switch { 0 => 7, 2 => 8, _ => -1 };

    /// <summary>Advances the script by <paramref name="delta"/> seconds; the sections a server would have sent: (tag, payload).</summary>
    public IEnumerable<(byte Tag, byte[] Payload)> Step(double delta)
    {
        _clock += delta;
        while (_next <= _clock)
        {
            double t = _next;
            _next += 1.0 / TicksPerSecond;
            if (_phase % 20 == 0)
                foreach (var s in Rosters(_rosterRound == 0 ? -1 : _rosterRound % _owners)) yield return (SquadWire.RosterTag, s);
            if (_phase % 20 == 0) _rosterRound++;
            var marines = Marines(t);
            if (_events.Count > 0) { yield return (SquadWire.EventsTag, SquadWire.EncodeEvents(_events)); _events.Clear(); }
            yield return (SquadWire.StateTag, SquadWire.EncodeState((byte)_phase, marines));
            _phase++;
        }
    }

    IEnumerable<byte[]> Rosters(int only)
    {
        for (int o = 0; o < _owners; o++)
        {
            if (only >= 0 && o != only) continue;
            var list = new List<SquadRosterWire>();
            for (int s = 0; s < _per; s++)
            {
                int name = (o * 9 + s) % Names.Length;
                list.Add(new SquadRosterWire((byte)s, (byte)(o * 9 + s), Names[name], SquadClass[o], 10, 11, 12, OwnPieces[o],
                    [(ushort)(40 + name * 7), (ushort)(name % 5), (ushort)(name % 3 == 0 ? 1 : 0), (ushort)(name * 2), (ushort)(name % 4), (ushort)(name % 3), (ushort)(name % 6), (ushort)(2 + name % 9)]));
            }
            yield return SquadWire.EncodeRoster(new SquadRosterSection(HandleOf(o), (byte)PlayerOf(o), false, list));
        }
    }

    List<SquadMarineWire> Marines(double t)
    {
        var list = new List<SquadMarineWire>(Count);
        double inCycle = t % Cycle;
        for (int o = 0; o < _owners; o++)
            for (int s = 0; s < _per; s++)
            {
                int index = o * 9 + s;
                var (p, yaw, firing, air, moving) = Place(o, s, t);
                bool down = s == DownSlot(o) && inCycle >= DownAt + o * 0.5 && inCycle < ReviveFrom + ReviveSeconds + o * 0.5;
                double shift = o * 0.5;
                bool reviving = down && inCycle >= ReviveFrom + shift;
                float share = reviving ? (float)Math.Clamp((inCycle - ReviveFrom - shift) / ReviveSeconds, 0.0, 1.0) : 0f;
                float downedLeft = down ? (float)(20.0 - (inCycle - DownAt - shift)) : 0f;
                if (down != _down[index])
                {
                    _events.Add(new SquadEventWire(down ? SquadEventKind.Downed : SquadEventKind.Revived, (byte)index, down ? (byte)255 : (byte)1));
                    _down[index] = down;
                }
                list.Add(SquadWire.Make(index, down ? (byte)2 : (byte)1, firing && !down, false, reviving, 0, air && !down, p, yaw, down ? 0f : 1f, downedLeft, share,
                    firing && !down ? 1001 + (index % 12) : -1));
            }
        return list;
    }
}
