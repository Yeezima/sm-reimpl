using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// fix/grenades probe on the player's install (read-only; skipped without it, never in CI): every thrown item with a rigid body (frag, blind grenade,
/// the Ork's stikkbomb) thrown on flat ground at several pitches; where it first touches the ground, how far it travels after that (to the detonation),
/// and how high the hop goes. Printed with the data's rigid values and with the owner's shorter bounce (OwnerBalance).
/// </summary>
public class GrenadeBounceProbe(ITestOutputHelper output)
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (needs the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void Bounce_distances_of_the_thrown_items()
    {
        foreach (bool owner in new[] { false, true })
        {
            var c = GameplayTuning.Load(Game.Value!, OrkBalanceMode.Parity, ownerBalance: owner).Config;
            output.WriteLine(owner ? "with the owner's changes (OwnerBalance):" : "the data's values (ownerBalance: false):");
            var seen = new HashSet<string>();
            for (int cls = 0; cls < c.Classes.Count; cls++)
            {
                var k = c.Classes[cls];
                for (int e = 0; e < k.EquipmentChoices.Count; e++)
                {
                    var item = c.Thrown[k.EquipmentChoices[e]];
                    if (item.Projectile.Rigid is not { } rigid || !seen.Add(item.Name)) continue;
                    output.WriteLine($"  {item.Name} (class {k.Name}): speed {item.Projectile.Speed}, ground kept {rigid.GroundHorizontalReduction} across / {rigid.GroundVerticalReduction} up, " +
                                     $"bounces {item.Projectile.MaxBounces}, ground fuse {item.Projectile.GroundLifetime} s");
                    foreach (float pitch in new[] { -0.9f, -0.5f, -0.25f, 0f, 0.25f, 0.5f })
                    {
                        var (land, end, hop, ticks) = Throw(c, cls, e, pitch);
                        output.WriteLine($"    pitch {pitch,5:0.00} rad: lands {land,5:0.0} m out, goes off {end,5:0.0} m out: {end - land,4:0.0} m after landing (hop {hop:0.00} m high; {ticks} ticks from the release)");
                    }
                }
            }
        }
    }

    /// <summary>One throw from rest facing +Y on flat ground (no level): horizontal distances of the first ground contact and the detonation, the hop's peak.</summary>
    static (float Land, float End, float Hop, int Ticks) Throw(SimConfig c, int cls, int equipment, float pitch)
    {
        var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        world.Spawn(1, cls, equipmentChoice: equipment);
        PlayerInput In(InputButtons b) => new(0, 0, 0, 0f, b, pitch);
        world.Step(_ => In(InputButtons.Grenade));
        float land = float.NaN, hop = 0f; int ticks = 0; bool launched = false;
        for (int t = 0; t < 900; t++)
        {
            world.Step(_ => In(InputButtons.None));
            if (world.LastDetonations.Count > 0)
            {
                var d = world.LastDetonations[0].Position;
                return (land, new Vector2(d.X, d.Y).Length(), hop, ticks);
            }
            if (world.Projectiles.Count == 0) continue;
            launched = true; ticks++;
            var p = world.Projectiles[0];
            if (float.IsNaN(land) && (p.Landed || p.Bounces > 0)) land = new Vector2(p.Position.X, p.Position.Y).Length();
            else if (!float.IsNaN(land)) hop = MathF.Max(hop, p.Position.Z);
        }
        return (land, launched ? float.NaN : 0f, hop, ticks);
    }
}
