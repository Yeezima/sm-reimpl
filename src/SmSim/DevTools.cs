namespace SmSim;

/// <summary>
/// The developer tools' switches (owner's request, 2026-11-17: an in-game panel for testing on a local server). Ours, not the original; never on in a real match: only a
/// server started with <c>--dev-tools</c> that listens on loopback alone ever sets one (<c>GameServer.DevTools.cs</c>). Not in the config hash (server-side state; the
/// client mirrors the flags only to predict its own ammunition and jump power the way the server will have them).
/// </summary>
[Flags]
public enum DevToolsFlags : byte
{
    None = 0,
    /// <summary>Every player and bot takes no damage (<see cref="World.DamageTakenScale"/> is 0).</summary>
    Invincible = 1,
    /// <summary>Test bots (clients that said so in their Hello) get a neutral input every tick: they stand still, do not aim and do not fire (the server's input step).</summary>
    FreezeBots = 2,
    /// <summary>Clips, reserves, heat and the thrown items never run out (<see cref="DevTools.TopUp"/>, after every tick).</summary>
    InfiniteAmmo = 4,
    /// <summary>The jump pack's power never runs low (<see cref="DevTools.TopUp"/>, after every tick).</summary>
    InfiniteJump = 8,
    /// <summary>The current match ends neither at its time limit nor at its score limit (the match clock and <c>World.CheckScoreLimit</c> hold); cleared by the world when the next match starts.</summary>
    Indefinite = 16,
    /// <summary>Nobody waits out a death timer: a dead player respawns on the next tick (<see cref="World.ApplyDevTools"/>).</summary>
    InstantRespawn = 32,
}

public static class DevTools
{
    /// <summary>Every flag a command may set.</summary>
    public const DevToolsFlags All = DevToolsFlags.Invincible | DevToolsFlags.FreezeBots | DevToolsFlags.InfiniteAmmo | DevToolsFlags.InfiniteJump | DevToolsFlags.Indefinite | DevToolsFlags.InstantRespawn;

    /// <summary>The time scales the panel offers (index on the wire, protocol 80): normal, half, quarter, a tenth, paused.</summary>
    public static readonly float[] TimeScales = [1f, 0.5f, 0.25f, 0.1f, 0f];

    /// <summary>True when <paramref name="flag"/> is exactly one known flag.</summary>
    public static bool IsSingleFlag(DevToolsFlags flag) => flag != DevToolsFlags.None && (flag & All) == flag && (((byte)flag) & ((byte)flag - 1)) == 0;

    /// <summary>
    /// The player with what the ammunition and jump-pack flags promise: every weapon's clip and reserve full (a weapon the class does not carry stays as it is), every heat gauge cold and no weapon overheated
    /// (our choice: infinite ammunition also means a plasma gun never overheats), the thrown items at least at their spawn count, the jump pack at full power. A reload under way is left to finish
    /// (it moves nothing from a full reserve). A dead player and a call without either flag return the player as it is.
    /// </summary>
    public static PlayerState TopUp(PlayerState s, SimConfig c, DevToolsFlags flags)
    {
        if (!s.Alive || (flags & (DevToolsFlags.InfiniteAmmo | DevToolsFlags.InfiniteJump)) == 0) return s;
        if ((flags & DevToolsFlags.InfiniteAmmo) != 0)
        {
            var inHand = Loadouts.Weapon(c, s, s.Slot);
            var other = Loadouts.Weapon(c, s, Loadouts.Other(s.Slot));
            var third = Loadouts.Weapon(c, s, Loadouts.Third(s.Slot));
            s = s with
            {
                Clip = inHand?.ClipSize ?? s.Clip, Reserve = inHand?.ReserveSize ?? s.Reserve,
                OtherClip = other?.ClipSize ?? s.OtherClip, OtherReserve = other?.ReserveSize ?? s.OtherReserve,
                ThirdClip = third?.ClipSize ?? s.ThirdClip, ThirdReserve = third?.ReserveSize ?? s.ThirdReserve,
                Heat = 0f, Overheated = false, OtherHeat = 0f, OtherOverheated = false, ThirdHeat = 0f, ThirdOverheated = false,
                Grenades = Math.Max(s.Grenades, Loadouts.Equipment(c, s)?.SpawnCount ?? 0),
            };
        }
        if ((flags & DevToolsFlags.InfiniteJump) != 0 && Loadouts.JumpPack(c, s) is { } pack)
            s = s with { JumpPower = pack.MaxPower };
        return s;
    }
}

public sealed partial class World
{
    /// <summary>The developer tools in force (ours; <see cref="DevToolsFlags.None"/> unless a loopback-only <c>--dev-tools</c> server set them). Server-side only; not in the config hash.</summary>
    public DevToolsFlags DevFlags { get; set; }

    /// <summary>After a tick: the ammunition and jump-pack flags' top-up of every living player, and the instant respawn's end of every death timer (nothing, and no allocation, with neither flag).</summary>
    public void ApplyDevTools()
    {
        var flags = DevFlags;
        if ((flags & (DevToolsFlags.InfiniteAmmo | DevToolsFlags.InfiniteJump | DevToolsFlags.InstantRespawn)) == 0) return;
        foreach (int id in _players.Keys.ToList())
        {
            var s = DevTools.TopUp(_players[id], Config, flags);
            if ((flags & DevToolsFlags.InstantRespawn) != 0 && !s.Alive && s.RespawnLeft > 0f) s = s with { RespawnLeft = 0f };   // the world respawns it on the next tick
            _players[id] = s;
        }
    }
}
