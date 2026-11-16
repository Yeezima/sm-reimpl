using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Squad;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Wiring;

/// <summary>
/// The squad strength table (squad-strength ruling, 2026-11-17; W17 copies it next to the Ork tuning table): a Marine's damage against a bolter player's, seconds for a Marine to kill a Boy, an 'Ard Boy and a Nob, seconds for five
/// Boyz and for one Nob to down a lone Marine at levels 1, 10 and 20, and the two readable rows (a full squad of nine against a wave of 40 Boyz and 4 Nobz with the owner absent, and with the owner reviving but not shooting).
/// Every number is an estimate for the owner's playtest. Inputs: the spec's weapon rows (bolter 22 / 0.133 s, heavy bolter 35 / 0.233 s, no reload: a lower bound), the player's body of 100 health and 100 armour (armour regenerates after
/// 7 s at 33 a second, health does not in a battle), the Ork rows of <see cref="MusouRules.Default"/> scaled by the level (<see cref="WarfrontOrkScaling"/>, one human). Nothing is read from the install. The target ranges are printed, not asserted: a miss is reported, not retuned.
/// </summary>
public class SquadStrengthTable(ITestOutputHelper output)
{
    const float Health = 100f, Armour = 100f, ArmourDelay = 7f, ArmourRate = 33f;
    const int Rate = 30;

    static readonly (string Name, SquadWeapon Weapon)[] Classes =
    [
        ("Tactical (bolter)", new SquadWeapon(60f, 22f, 0.133f, SquadClassRules.HitFullAuto)),
        ("Devastator (heavy bolter)", new SquadWeapon(60f, 35f, 0.233f, SquadClassRules.HitFullAuto)),
        ("Assault (melee, placeholder blow)", SquadWeapon.Blade),
    ];

    static SquadLoadout Body(SquadWeapon weapon) => new()
    {
        HealthMax = Health, ArmourMax = Armour, ArmourRegenDelay = ArmourDelay, ArmourRegenRate = ArmourRate, Weapon = weapon,
    };

    /// <summary>One scene: <paramref name="marines"/> Marines (index order, one owner) against a wave; the owner absent (no human in the list) or reviving (walks to the nearest downed Marine and holds Use, takes no damage, never shoots).</summary>
    static (double WipedAt, int Standing, int Kills, int Downs) Scene(int level, int boyz, int nobz, int marines, SquadWeapon weapon, bool ownerReviving, double maxSeconds, float spawnRing = 32f)
    {
        var pool = new MusouSim(null);
        var rosters = new SquadRosters();
        rosters.AddOwner(1UL);
        while (rosters.Count(1UL) < marines) rosters.Recruit(1UL);
        rosters.Mirror(1UL, Body(weapon));
        var squad = new WarfrontSquadSim(rosters, new MusouSquadArena(pool));
        squad.BeginBattle([(1UL, 1, Vector3.Zero)]);
        squad.Order(1, new SmSim.Extras.Musou.Campaign.TfwCommandRecord(1, SmSim.Extras.Musou.Campaign.TfwCommand.Hold, 0));   // the squad holds its landing: the Orks come to it
        var scale = WarfrontOrkScaling.For(level, 1);
        int total = boyz + nobz;
        for (int i = 0; i < total; i++)
        {
            var type = i < boyz ? MusouEnemyType.Boy : MusouEnemyType.Nob;
            float angle = i * 2.399963f, ring = spawnRing + (i % 5) * 2f;
            pool.Spawn(type, new Vector3(MathF.Cos(angle) * ring, MathF.Sin(angle) * ring, 0f), 0f, pool.Rules.Of(type).Health * scale.Health);
        }
        var targets = new MusouTarget[64];
        var attacks = new List<MusouAttack>();
        var human = new Vector3(-1f, 0f, 0f);
        var humans = new SquadHuman[1];
        uint tick = 0;
        int kills = 0, downs = 0;
        double wiped = -1;
        int steps = (int)(maxSeconds * Rate);
        for (int k = 0; k < steps; k++)
        {
            int target = -1;
            if (ownerReviving)
            {
                float best = float.MaxValue;
                for (int i = 0; i < marines; i++)
                    if (squad.View(i) is { State: SquadState.Downed } v && Vector3.Distance(v.Position, human) < best) { best = Vector3.Distance(v.Position, human); target = i; }
                if (target >= 0)
                {
                    var to = squad.View(target)!.Value.Position - human;
                    float d = new Vector2(to.X, to.Y).Length();
                    if (d > SquadRules.PlayerReviveReach * 0.5f) human += Vector3.Normalize(new Vector3(to.X, to.Y, 0f)) * MathF.Min(5.5f / Rate, d);
                    else { }
                    if (d > SquadRules.PlayerReviveReach) target = -1;   // not yet within reach: no Use
                }
                humans[0] = new SquadHuman(1, human, true, false, target);
            }
            int n = squad.AppendTargets(targets);
            pool.BeginTick(++tick);
            attacks.Clear();
            pool.Step(targets.AsSpan(0, n), null, true, attacks);
            foreach (var a in attacks) squad.Hit(a.Player, a.Damage * scale.Damage, a.From);
            squad.Step(tick, ownerReviving ? humans : []);
            kills += squad.Kills.Count; downs += squad.Downs.Count;
            if (wiped < 0 && !squad.AnyStanding) { wiped = (k + 1) / (double)Rate; if (!ownerReviving || !squad.AnyReviveWindowOpen) break; }
            if (wiped < 0 && pool.Alive == 0) break;
        }
        int standing = Enumerable.Range(0, marines).Count(i => squad.StateOf(i) == SquadState.Standing);
        return (wiped, standing, kills, downs);
    }

    [Fact]
    public void Print_the_squad_strength_table()
    {
        var rules = MusouRules.Default;
        float bolterDps = WarfrontTuningTable.BolterDamage / WarfrontTuningTable.BolterInterval;
        output.WriteLine($"Squad strength (DamageDealt {SquadRules.DamageDealt:0.00}, DamageTaken {SquadRules.DamageTaken:0.00}, Marine health regeneration off, armour regeneration {ArmourDelay:0} s then {ArmourRate:0} a second); body {Health:0} health + {Armour:0} armour; weapon rows from the spec, 100 % accuracy for the player.");
        output.WriteLine($"A bolter player: {bolterDps:0} damage a second.");
        output.WriteLine("Marine damage a second (squad weapon x hit chance x DamageDealt), share of the bolter player's:");
        foreach (var (name, w) in Classes)
        {
            double dps = w.Damage * w.HitChance * SquadRules.DamageDealt / w.Interval;
            output.WriteLine($"  {name,-36} {dps,6:0.0}  = {dps / bolterDps,5:0.00} of a player; nine Marines = {9 * dps / bolterDps,4:0.0} players");
        }
        output.WriteLine("Seconds for one Marine to kill, by level (Ork health x level scale), Tactical / Devastator:");
        foreach (int level in new[] { 1, 10, 20 })
        {
            var s = WarfrontOrkScaling.For(level, 1);
            string Cell(MusouEnemyType t) => string.Join(" / ", Classes.Take(2).Select(c => (rules.Of(t).Health * s.Health / (c.Weapon.Damage * c.Weapon.HitChance * SquadRules.DamageDealt / c.Weapon.Interval)).ToString("0.0")));
            output.WriteLine($"  level {level,2}: Boy {Cell(MusouEnemyType.Boy)}  'Ard Boy {Cell(MusouEnemyType.ArdBoy)}  Nob {Cell(MusouEnemyType.Nob)}");
        }
        output.WriteLine("Seconds for Orks to down one lone Marine (it does not fight back):");
        var none = new SquadWeapon(30f, 0.0001f, 1f, 0f);
        foreach (int level in new[] { 1, 10, 20 })
        {
            var five = Scene(level, 5, 0, 1, none, false, 300, spawnRing: 4f);
            var nob = Scene(level, 0, 1, 1, none, false, 300, spawnRing: 4f);
            output.WriteLine($"  level {level,2}: five Boyz {Show(five.WipedAt)}  one Nob {Show(nob.WipedAt)}");
            Assert.True(five.WipedAt > 0 && nob.WipedAt > 0, "a lone Marine is downed inside five minutes");
        }
        output.WriteLine("A full squad of nine against a wave of 40 Boyz and 4 Nobz (target: owner absent 60 to 150 s until the last Marine is down; owner reviving: holds at level 1, falls slowly at level 20):");
        foreach (var (name, w) in Classes)
            foreach (int level in new[] { 1, 10, 20 })
            {
                var absent = Scene(level, 40, 4, 9, w, false, 300);
                var revive = Scene(level, 40, 4, 9, w, true, 300);
                string target = absent.WipedAt < 0 ? "outlasts the run" : absent.WipedAt is >= 60 and <= 150 ? "in range" : "OUTSIDE 60-150 s";
                output.WriteLine($"  {name,-36} level {level,2}: owner absent: last Marine down at {Show(absent.WipedAt)} ({absent.Kills} Ork kills, {absent.Downs} knockdowns, {absent.Standing} standing at the end) [{target}]; owner reviving: {(revive.WipedAt < 0 ? $"holds ({revive.Standing} standing at 300 s, {revive.Kills} kills, {revive.Downs} knockdowns)" : $"all down at {Show(revive.WipedAt)} ({revive.Kills} kills)")}");
            }
    }

    static string Show(double seconds) => seconds < 0 ? "never" : $"{seconds:0.0} s";
}
