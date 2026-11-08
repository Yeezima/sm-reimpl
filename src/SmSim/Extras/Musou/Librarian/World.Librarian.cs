using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou": the Librarian in the World (<see cref="LibrarianRules"/>, <see cref="LibrarianPsyker"/>). Each tick the core hands the sampled input of every player to <see cref="LibrarianSampled"/>; the Musou step
/// (<see cref="LibrarianStep"/>) then runs the powers of every living player whose class is the Librarian (only in a Playing match). A melee swing of his that strikes an enemy takes stress off
/// (<see cref="LibrarianMeleeBegin"/> and <see cref="LibrarianMeleeEnd"/> around the cleave). Nothing here is in the core snapshot: the stress and the cooldowns go out on Musou Mode's tag 16.
/// </summary>
public sealed partial class World
{
    readonly Dictionary<int, LibrarianPsyker> _librarians = new();
    readonly Dictionary<int, PlayerInput> _librarianInputs = new();
    readonly List<int> _librarianGone = new();

    /// <summary>The Librarian players' powers by player id (alive or not; the ones whose class changed are dropped at the next step).</summary>
    public IReadOnlyDictionary<int, LibrarianPsyker> Librarians => _librarians;

    /// <summary>The psyker of player <paramref name="id"/>, or null when he is not a Librarian now.</summary>
    public LibrarianPsyker? LibrarianOf(int id) => _librarians.GetValueOrDefault(id);

    void LibrarianSampled(int id, in PlayerInput input)
    {
        if (_musou is null || !_players.TryGetValue(id, out var p) || !LibrarianRules.IsLibrarian(Config, p.Class)) return;
        _librarianInputs[id] = input;
    }

    void LibrarianStep()
    {
        var musou = _musou!;
        MusouBeginTick();
        float dt = 1f / Math.Max(1, Config.TickRate);
        _librarianGone.Clear();
        foreach (var id in _librarians.Keys) if (!_players.TryGetValue(id, out var q) || !LibrarianRules.IsLibrarian(Config, q.Class)) _librarianGone.Add(id);
        foreach (int id in _librarianGone) _librarians.Remove(id);
        foreach (var (id, p) in _players)
        {
            if (!LibrarianRules.IsLibrarian(Config, p.Class)) continue;
            if (!_librarians.TryGetValue(id, out var psyker)) _librarians[id] = psyker = new LibrarianPsyker();
            if (!p.Alive) { psyker.Reset(); continue; }
            var press = default(LibrarianPress);
            float pitch = 0f;
            if (_state == MatchState.Playing && _librarianInputs.TryGetValue(id, out var input) && input.Tick == Tick)
            {
                press = new LibrarianPress(input.Buttons.HasFlag(InputButtons.Fire), input.Buttons.HasFlag(InputButtons.Grenade), input.Buttons.HasFlag(InputButtons.Reload));
                pitch = input.Pitch;
            }
            var eye = p.Position + new Vector3(0f, 0f, Config.EyeHeight);
            psyker.Step(musou, new LibrarianCaster(id, p.Position, eye, p.Yaw, pitch), press, dt, Blocked);
        }
        _librarianInputs.Clear();
    }

    /// <summary>The count of melee hits on enemies by <paramref name="attacker"/> so far this tick (read before and after the cleave).</summary>
    int LibrarianMeleeBegin(int attacker)
    {
        if (!_players.TryGetValue(attacker, out var p) || !LibrarianRules.IsLibrarian(Config, p.Class)) return -1;
        if (!_librarians.ContainsKey(attacker)) _librarians[attacker] = new LibrarianPsyker();
        return MeleeHitEvents(attacker);
    }

    void LibrarianMeleeEnd(int attacker, int before)
    {
        if (before >= 0 && _librarians.TryGetValue(attacker, out var psyker) && MeleeHitEvents(attacker) > before) psyker.MeleeHit();
    }

    int MeleeHitEvents(int attacker)
    {
        int n = 0;
        foreach (var e in _musou!.Events)
            if (e.Kind == MusouEventKind.Hit && e.Player == attacker && e.Value == 3 && e.Tick == Tick) n++;
        return n;
    }
}
