using System.Numerics;
using System.Text.RegularExpressions;
using SmContent;
using SmSim;

namespace SmTool;

/// <summary>
/// <c>smtool hitbox-probe &lt;game-dir|auto&gt; [--clips &lt;regex&gt;] [--quiet]</c> (fix/hitboxes, test aid): how far the drawn body of a jump-pack marine
/// in flight is from the server's hit volumes. For each flight clip (<c>SM_JMP_Zero*_&lt;dir&gt;_Aim_&lt;aim&gt;</c>, posed by the vertical angle as the
/// client does: fraction 0 rising, 0.5 level, 1 falling; every 0.1) the rig's volumes are moved with their bones (<see cref="PosedHitVolumes"/>: the
/// drawn body) and compared with the rest volumes (the server before) and the server's flight set (<see cref="FlightHitVolumes"/>, the nearest of its
/// <see cref="FlightHitVolumes.Steps"/> fractions): each volume's centre shift, and an aim test (a shooter 20 m away in 8 directions x 3 elevations
/// aims at the centre of every drawn volume, and of the torso's: head, chest, pelvis). Read-only.
/// </summary>
static class HitboxCommands
{
    public static int Probe(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        var notes = new List<string>();
        var rig = PosedHitVolumes.Load(gameDir, notes);
        var flight = rig is null ? null : FlightHitVolumes.Build(rig, notes);
        foreach (var n in notes) Console.WriteLine($"note: {n}");
        if (rig is null) { Console.Error.WriteLine("hit volumes or rig not readable"); return 1; }
        bool quiet = rest.Contains("--quiet");
        string pattern = rest.SkipWhile(a => a != "--clips").Skip(1).FirstOrDefault() ?? @"^SM_JMP_Zero(Lo)?_[FBLRU]_Aim_[UFD]$";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Console.WriteLine($"rest volumes: {rig.Rest.Count} ({string.Join(", ", rig.Rest.Select(v => v.Name))})");
        Console.WriteLine($"server flight poses: {(flight is null ? "none" : $"{flight.Poses.Count} clips x {FlightHitVolumes.Steps} fractions")}");
        var totals = new Dictionary<string, Tally>();
        foreach (var clip in rig.Character.AnimationNames.Where(a => regex.IsMatch(a) && !a.Contains("_TH_", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            bool low = clip.Contains("ZeroLo", StringComparison.OrdinalIgnoreCase);
            var m = Regex.Match(clip, @"_([FBLRU])_Aim_([UFD])", RegexOptions.IgnoreCase);
            var pose = flight?.Poses.FirstOrDefault(p => p.Low == low && p.Dir == char.ToUpperInvariant(m.Groups[1].Value[0]) && p.Aim == char.ToUpperInvariant(m.Groups[2].Value[0]));
            if (!quiet) Console.WriteLine($"== {clip}");
            for (int k = 0; k <= 10; k++)
            {
                float f = k / 10f;
                var drawn = rig.Pose(rig.ClipPose(clip, f));
                var shifts = rig.Rest.Select((v, i) => (v.Name, Shift: PosedHitVolumes.Centre(drawn[i]) - PosedHitVolumes.Centre(v))).ToArray();
                var server = pose?.Sets[pose.IndexOf(f)];
                var serverShift = server is null ? 0f : drawn.Select((v, i) => (PosedHitVolumes.Centre(v) - PosedHitVolumes.Centre(server[i])).Length()).Max();
                var all = AimTest(drawn, rig.Rest, server, torso: false);
                var torso = AimTest(drawn, rig.Rest, server, torso: true);
                foreach (var key in new[] { low ? "low set" : "high set", "all clips" })
                {
                    var t = totals.GetValueOrDefault(key) ?? (totals[key] = new Tally());
                    t.Add(all, torso, shifts.Where(s => IsTorso(s.Name)).Max(s => s.Shift.Length()), serverShift);
                }
                if (quiet || k % 2 == 1) continue;
                string Of(string name) => shifts.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is var s && s.Name is not null
                    ? $"{s.Shift.Length():0.00} m ({s.Shift.X:+0.00;-0.00},{s.Shift.Y:+0.00;-0.00},{s.Shift.Z:+0.00;-0.00})" : "-";
                Console.WriteLine($"  {f:0.0}: rest to drawn: chest {Of("Chest")}, head {Of("Head")}, pelvis {Of("Pelvis")}; server set to drawn worst {serverShift:0.00} m; " +
                                  $"shots at the drawn torso: rest {Pct(torso.Before, torso.Shots)}, server {Pct(torso.Server, torso.Shots)}; at every drawn volume: rest {Pct(all.Before, all.Shots)}, server {Pct(all.Server, all.Shots)}");
            }
        }
        Console.WriteLine();
        foreach (var (k, t) in totals)
            Console.WriteLine($"total {k}: shots at the drawn torso hit the rest volumes {Pct(t.Torso.Before, t.Torso.Shots)}, the server's flight volumes {Pct(t.Torso.Server, t.Torso.Shots)} ({t.Torso.Shots}); " +
                              $"at every drawn volume rest {Pct(t.All.Before, t.All.Shots)}, flight {Pct(t.All.Server, t.All.Shots)} ({t.All.Shots}); torso centres off the rest pose up to {t.WorstTorso:0.00} m, " +
                              $"any centre off the server's flight set up to {t.WorstServer:0.00} m (self-check {Pct(t.All.Self, t.All.Shots)})");
        return 0;
    }

    sealed class Tally
    {
        public (int Before, int Server, int Self, int Shots) All, Torso;
        public float WorstTorso, WorstServer;
        public void Add((int Before, int Server, int Self, int Shots) all, (int Before, int Server, int Self, int Shots) torso, float worstTorso, float worstServer)
        {
            All = (All.Before + all.Before, All.Server + all.Server, All.Self + all.Self, All.Shots + all.Shots);
            Torso = (Torso.Before + torso.Before, Torso.Server + torso.Server, Torso.Self + torso.Self, Torso.Shots + torso.Shots);
            WorstTorso = MathF.Max(WorstTorso, worstTorso); WorstServer = MathF.Max(WorstServer, worstServer);
        }
    }

    static string Pct(int a, int b) => b == 0 ? "-" : $"{100.0 * a / b:0.0}%";

    static bool IsTorso(string name) => name.StartsWith("Head", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Chest", StringComparison.OrdinalIgnoreCase)
                                        || name.StartsWith("Pelvis", StringComparison.OrdinalIgnoreCase);

    /// <summary>Shots from 20 m in 8 azimuths x 3 elevations at the centre of every drawn (posed) volume (or the torso's): how many each volume list takes.</summary>
    static (int Before, int Server, int Self, int Shots) AimTest(IReadOnlyList<HitVolume> drawn, IReadOnlyList<HitVolume> rest, IReadOnlyList<HitVolume>? server, bool torso)
    {
        int restHits = 0, serverHits = 0, self = 0, shots = 0;
        foreach (var v in drawn)
        {
            if (torso && !IsTorso(v.Name)) continue;
            var target = PosedHitVolumes.Centre(v);
            for (int a = 0; a < 8; a++)
                foreach (float elevation in new[] { -30f, 0f, 30f })
                {
                    float az = a * MathF.PI / 4f, el = elevation * MathF.PI / 180f;
                    var toShooter = new Vector3(MathF.Cos(el) * MathF.Cos(az), MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el));
                    var origin = target + toShooter * 20f;
                    var direction = -toShooter;
                    shots++;
                    if (HitVolumes.Raycast(rest, origin, direction, Vector3.Zero, 0f, 40f).Zone != HitZone.None) restHits++;
                    if (HitVolumes.Raycast(drawn, origin, direction, Vector3.Zero, 0f, 40f).Zone != HitZone.None) self++;
                    if (server is not null && HitVolumes.Raycast(server, origin, direction, Vector3.Zero, 0f, 40f).Zone != HitZone.None) serverHits++;
                }
        }
        return (restHits, serverHits, self, shots);
    }
}
