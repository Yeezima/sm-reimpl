using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The big Orks outside the caps and twice over (the owner, 2026-11-10: "double the amount of scaled up in size orks - whatever those are let them exist independent of the cap. I want their numbers doubled -
/// so 2x of them spawn compared to before"; <see cref="TfwBattleRules.Big"/>). Every big Ork the battle makes outside a garrison or an army is kept in a list the crowds' cap leaves out (<see cref="Counted"/>);
/// the armies leave theirs out of their real cap the same way (<see cref="ArmyBigAlive"/>). The pool's room still counts every body. A change raises <see cref="TfwRules.Version"/>.
/// </summary>
public sealed partial class TfwBattle
{
    readonly List<ushort> _bigs = new();

    /// <summary>The big Orks alive outside garrisons and armies (left out of the crowds' cap).</summary>
    public int BigsAlive { get; private set; }
    /// <summary>Big Orks made since the start (twins included): tests and the profile.</summary>
    public int BigsMade { get; private set; }
    /// <summary>Of those, the twins (the copies beside a big Ork made before 2026-11-10 too).</summary>
    public int TwinsMade { get; private set; }

    /// <summary>A big Ork made outside a garrison and an army: kept off the crowds' cap.</summary>
    void TrackBig(ushort id)
    {
        if (id == 0 || !TfwBattleRules.BigOutsideCaps) return;
        _bigs.Add(id);
        BigsAlive++;
        BigsMade++;
    }

    /// <summary>Forgets the dead (each tick, before the cap is counted).</summary>
    void TidyBigs()
    {
        for (int k = _bigs.Count - 1; k >= 0; k--)
            if (!_enemies.TryGet(_bigs[k], out var e) || !e.Alive) _bigs.RemoveAt(k);
        BigsAlive = _bigs.Count;
    }

    /// <summary>
    /// The copies of a big Ork just made at <paramref name="at"/> (<see cref="TfwBattleRules.BigTwins"/> less one): the same kind and health, a little aside (1.5 m round him), in the same group with the same
    /// aggro (<paramref name="group"/> below 0: free), and tracked off the cap. Their ids go to <paramref name="into"/> when given.
    /// </summary>
    void SpawnTwins(MusouEnemyType type, Vector3 at, float yaw, float health, int group, float aggro, List<ushort>? into = null, bool track = true)
    {
        if (!TfwBattleRules.Big(type)) return;
        for (int n = 1; n < TfwBattleRules.BigTwins; n++)
        {
            float angle = yaw + MathF.PI / 2f + n * 2.399963f;
            var spot = Ground(at + new Vector3(MathF.Cos(angle) * 1.5f, MathF.Sin(angle) * 1.5f, 0f), at);
            ushort id = group >= 0 ? _enemies.SpawnLeashed(type, spot, yaw, health, group, aggro) : _enemies.Spawn(type, spot, yaw, health);
            if (id == 0) return;
            if (track) TrackBig(id); else BigsMade++;
            TwinsMade++;
            into?.Add(id);
        }
    }
}
