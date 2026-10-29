using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmFormats.Fx;

namespace SmClient;

/// <summary>
/// The ground pound's impact on the ground (M41), shared by the networked view and the effects test view. The pound plays the original effect the wargear's
/// <c>actn_gp_hit</c> names (<c>jumppack_land_heavy</c>: a 4 m bolter-crater decal and a 6 m crack decal, a flash and a light, dust sprays, debris sprites and
/// ground shockwave sprites; all the data's), a <see cref="Ring"/> at the radius of its blast (**ours**: no original decal marks it) and a handful of
/// <see cref="GroundShards"/> (**ours**: no debris mesh exists in the effects package). Landings play the data's landing effect only.
/// </summary>
public static class GroundPoundFx
{
    /// <summary>How long the hit-radius ring lies on the ground, with the last third fading out (ours; the data's crater decals last 8 s).</summary>
    public const float RingSeconds = 10f;

    /// <summary>
    /// How much larger than the data's scale the light landing's dust plays under a pound (**ours**: the heavy effect's own dust is a few clouds thrown one way; the light landing's sprays
    /// and debris sprites, also the data's, fill the ring around the impact).
    /// </summary>
    public const float DustScale = 2.8f;   // feel/jump-pack (the owner, 2026-10-29: "intensify the effect"): 2.2 in M44, 1.6 in M41

    /// <summary>
    /// M44 (ours): the 15 m landing's effect (<c>jumppack_land_normal</c>, the data's: more dust and grit) added under a pound at this scale; with the light landing's at
    /// <see cref="DustScale"/> the impact throws far more dust and debris sprites than the heavy effect alone.
    /// </summary>
    public const float GritScale = 1.8f;   // feel/jump-pack: 1.4 in M44

    /// <summary>The pound's effects with the light landing's added at <see cref="DustScale"/> and the normal landing's at <see cref="GritScale"/> (the landing's own scale times it).</summary>
    public static IReadOnlyList<FxPlay> WithDust(IReadOnlyList<FxPlay> pound, IReadOnlyList<FxPlay> lightLanding, IReadOnlyList<FxPlay>? normalLanding = null) =>
        [.. pound, .. lightLanding.Select(p => p with { Scale = (p.Scale <= 0f ? 1f : p.Scale) * DustScale }),
         .. (normalLanding ?? []).Where(p => !lightLanding.Any(l => l.Name == p.Name)).Select(p => p with { Scale = (p.Scale <= 0f ? 1f : p.Scale) * GritScale })];

    /// <summary>
    /// M44 (ours, the owner's request): the extra cracks under a pound: the data's crack decal (the heavy effect's <c>decal_standard_cracked_dif</c>, 6 m) laid again at these
    /// scales, each turned (degrees) and darkened (<see cref="CrackShade"/>), lasting <see cref="CrackSeconds"/> with the ring's fade.
    /// </summary>
    public static readonly (float Scale, float TurnDegrees)[] CrackLayers = [(1.7f, 37f), (2.5f, -71f)];
    public const float CrackShade = 0.45f, CrackSeconds = 12f, CrackDepth = 2.5f;

    /// <summary>The extra cracks' fade: the ring's, darkened by <see cref="CrackShade"/>.</summary>
    static readonly FxColourCurve CrackFade = new(
    [
        new FxColourKey(0f, new FxColour(115, 115, 115, 0)), new FxColourKey(0.02f, new FxColour(115, 115, 115, 255)),
        new FxColourKey(0.66f, new FxColour(115, 115, 115, 235)), new FxColourKey(1f, new FxColour(115, 115, 115, 0)),
    ]);

    /// <summary>Priority of these effects in the player's cull (a pound is rare and big: above shots and hits).</summary>
    public const int Priority = 3;

    /// <summary>
    /// Plays a jump-pack ground effect (<paramref name="plays"/>: the pound's or a landing's, from <see cref="JumpPackEffects"/>) on the ground at
    /// <paramref name="point"/> with the surface <paramref name="normal"/> (Godot space). Returns the effects started. With a <paramref name="ringRadius"/> above 0 the
    /// hit-radius ring is laid too, and with <paramref name="shards"/> the ground shards thrown (the pound only).
    /// </summary>
    public static List<FxEffect> Play(FxPlayer fx, IEnumerable<FxPlay> plays, Vector3 point, Vector3 normal, float ringRadius, GroundShards? shards, string surface, int seed)
    {
        var started = new List<FxEffect>();
        var up = normal.LengthSquared() < 1e-6f ? Vector3.Up : normal.Normalized();
        var axes = AxesFromY(up);
        foreach (var p in plays)
        {
            // The data's scale; the effect sits a hair above the surface so that its decals project onto it.
            var e = fx.Play(p.Name, point + up * 0.02f, axes, p.Scale <= 0f ? 1f : p.Scale, null, null, p.Lifetime, 0.0, false, Priority);
            if (e is null) continue;
            e.Collisions = true;
            e.SoftGround = true;   // feel/jump-pack: no hard lines where the ground or a body cuts the dust (FxBatch.GroundSoftScale)
            e.Life = Math.Max(e.Life, e.System.CollisionLife + 0.1f);   // what the collisions start (not the decals: their own clocks) outlives nothing that is cut off
            started.Add(e);
        }
        if (ringRadius > 0f && started.Count > 0 && PoundRing.Texture(ringRadius) is { } ring)
        {
            float size = PoundRing.SizeFor(ringRadius);
            fx.PlaceTextureDecal(ring, point, axes, size, size, 2.5f, RingSeconds, PoundRing.Fade);
        }
        if (ringRadius > 0f && started.Count > 0)
        {
            shards?.Spawn(point, up, surface, seed);
            LayCracks(fx, started, point, axes, seed);
        }
        return started;
    }

    /// <summary>The extra crack layers (<see cref="CrackLayers"/>) from the crack decals the started effects hold in their collision content (the data's textures and sizes).</summary>
    static void LayCracks(FxPlayer fx, List<FxEffect> started, Vector3 point, Basis axes, int seed)
    {
        var cracks = started.SelectMany(e => e.System.Roots).SelectMany(r => r.AllNodes()).OfType<CDecal>()
            .Where(d => d.Diffuse.Contains("crack", StringComparison.OrdinalIgnoreCase) && d.Width > 0f).GroupBy(d => d.Diffuse).Select(g => g.First()).ToList();
        foreach (var crack in cracks)
            foreach (var (scale, turn) in CrackLayers)
            {
                var basis = axes * new Basis(Vector3.Up, Mathf.DegToRad(turn + seed % 7 * 13f));
                fx.PlaceDataDecal(crack.Diffuse, point, basis, crack.Width * scale, crack.Height * scale, CrackDepth, CrackSeconds, CrackFade);
            }
    }

    /// <summary>
    /// M44: the ground burst of a jump-pack thrust (the data's: <c>jumppack_thrust</c> holds a "dropper" whose particles start, on the ground, a scorch decal, a flash ring,
    /// smoke and a light) at the ground under a lift-off or a dash start: only the dropper part, with its collisions (<see cref="FxEffect.GroundOnly"/>). Placing it straight
    /// under the pack at the start (no particle flight) is ours.
    /// </summary>
    public static int PlayBurst(FxPlayer fx, IEnumerable<FxPlay> thrust, Vector3 point, Vector3 normal)
    {
        var up = normal.LengthSquared() < 1e-6f ? Vector3.Up : normal.Normalized();
        var axes = AxesFromY(up);
        int n = 0;
        foreach (var p in thrust.GroupBy(p => p.Name).Select(g => g.First()))
        {
            var e = fx.Play(p.Name, point + up * 0.02f, axes, p.Scale <= 0f ? 1f : p.Scale, null, null, 0f, 0.0, false, Priority - 1);
            if (e is null) continue;
            e.Collisions = true; e.GroundOnly = true; e.SoftGround = true;
            e.Life = Math.Max(1.5, e.System.CollisionLife + 0.1f);
            n++;
        }
        return n;
    }

    /// <summary>
    /// feel/jump-pack (ours; the owner, 2026-10-29: "add a hit effect that shows roughly where the attack hitbox landed similar to the ground pound landing"): the sword
    /// slash's hit on the ground at <paramref name="point"/> (the attacker's feet when the cone resolves), swung along <paramref name="heading"/> (Godot space): the wedge of
    /// the melee cone (<see cref="SlashMark"/>, <paramref name="reach"/> m, ±<paramref name="halfAngle"/> degrees) laid as a decal for <see cref="SlashMarkSeconds"/>, the
    /// landing dust (<paramref name="dust"/>) at the cone's middle at <see cref="SlashDustScale"/>, and a small burst of chunks thrown forward within the cone.
    /// </summary>
    public static int PlaySlash(FxPlayer fx, IEnumerable<FxPlay> dust, Vector3 point, Vector3 normal, Vector3 heading, float reach, float halfAngle, GroundShards? shards, string surface, int seed)
    {
        var up = normal.LengthSquared() < 1e-6f ? Vector3.Up : normal.Normalized();
        var flat = heading - up * heading.Dot(up);
        if (flat.LengthSquared() < 1e-6f) flat = AxesFromY(up).Z;
        flat = flat.Normalized();
        // The decal's texture: row 0 (the cone's heading) at its local -Z, so +Z points back; X = Y x Z.
        var z = -flat;
        var basis = new Basis(up.Cross(z).Normalized(), up, z);
        int n = 0;
        if (SlashMark.Texture(reach, halfAngle) is { } mark)
        {
            float size = SlashMarkTexture.SizeFor(reach);
            fx.PlaceTextureDecal(mark, point + up * 0.02f, basis, size, size, 2.0f, SlashMarkSeconds, PoundRing.Fade);
            n++;
        }
        var middle = point + flat * (reach * 0.5f);
        foreach (var p in dust.GroupBy(p => p.Name).Select(g => g.First()))
        {
            var e = fx.Play(p.Name, middle + up * 0.02f, AxesFromY(up), (p.Scale <= 0f ? 1f : p.Scale) * SlashDustScale, null, null, p.Lifetime, 0.0, false, Priority);
            if (e is null) continue;
            e.Collisions = true; e.SoftGround = true;
            e.Life = Math.Max(e.Life, e.System.CollisionLife + 0.1f);
            n++;
        }
        shards?.Spawn(point + flat * 0.8f, up, surface, seed, SlashShards, SlashShardSpeed, flat);
        return n;
    }

    /// <summary>feel/jump-pack (ours): the slash mark's life (s; the pound's ring 10 s), the dust's scale at the cone's middle, the chunks (count, speed share).</summary>
    public const float SlashMarkSeconds = 8f, SlashDustScale = 1.5f, SlashShardSpeed = 0.55f;
    public static readonly (int Min, int Max) SlashShards = (8, 14);

    static Basis AxesFromY(Vector3 y)
    {
        var r = MathF.Abs(y.Y) > 0.99f ? Vector3.Right : Vector3.Up;
        var x = r.Cross(y).Normalized();
        return new Basis(x, y, x.Cross(y));
    }
}

/// <summary>feel/jump-pack: the slash mark (<see cref="SlashMarkTexture"/>) as a Godot texture, made on a worker thread like <see cref="PoundRing"/>'s (null until ready).</summary>
public static class SlashMark
{
    static readonly Dictionary<(int, int), Task<byte[]>> Pending = new();
    static readonly Dictionary<(int, int), ImageTexture> Cache = new();

    static (int, int) Key(float reach, float half) => ((int)MathF.Round(reach * 100f), (int)MathF.Round(half));

    public static void Request(float reach, float half)
    {
        var key = Key(reach, half);
        lock (Pending) if (!Pending.ContainsKey(key) && !Cache.ContainsKey(key)) Pending[key] = Task.Run(() => SlashMarkTexture.Render(reach, half));
    }

    public static ImageTexture? Texture(float reach, float half, bool wait = false)
    {
        var key = Key(reach, half);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Request(reach, half);
        Task<byte[]> task;
        lock (Pending) task = Pending[key];
        if (wait) task.Wait();
        if (!task.IsCompletedSuccessfully) return null;
        var image = Image.CreateFromData(SlashMarkTexture.Pixels, SlashMarkTexture.Pixels, false, Image.Format.Rgba8, task.Result);
        image.GenerateMipmaps();
        lock (Pending) Pending.Remove(key);
        return Cache[key] = ImageTexture.CreateFromImage(image);
    }
}

/// <summary>
/// The thin dark ring at the pound's blast radius as a Godot texture (M41; **ours**, see <see cref="PoundRingTexture"/>). The pixels are made on a worker thread
/// (<see cref="Request"/>, called when the radius is first known: about 30 ms of noise, never on the main thread); <see cref="Texture"/> only turns finished pixels into an
/// <see cref="ImageTexture"/> (main thread) and gives null while they are not ready, so that the first pound shows no ring rather than stalls a frame.
/// </summary>
public static class PoundRing
{
    static readonly Dictionary<int, Task<byte[]>> Pending = new();
    static readonly Dictionary<int, ImageTexture> Cache = new();

    /// <summary>The fade over the ring's life: in within a tenth of a second, held, out over the last third (colour keys as the data's decals do).</summary>
    public static readonly FxColourCurve Fade = new(
    [
        new FxColourKey(0f, new FxColour(255, 255, 255, 0)), new FxColourKey(0.02f, new FxColour(255, 255, 255, 255)),
        new FxColourKey(0.66f, new FxColour(255, 255, 255, 235)), new FxColourKey(1f, new FxColour(255, 255, 255, 0)),
    ]);

    /// <summary>The decal's side (m) for a blast radius (clamped to 1 to 30 m).</summary>
    public static float SizeFor(float radius) => PoundRingTexture.SizeFor(PoundRingTexture.ClampRadius(radius));

    static int Key(float radius) => (int)MathF.Round(PoundRingTexture.ClampRadius(radius) * 100f);

    /// <summary>Starts making the pixels for a radius on a worker thread (once per radius); ignores a radius that is no number above 0.</summary>
    public static void Request(float radius)
    {
        float r = PoundRingTexture.ClampRadius(radius);
        if (r <= 0f) return;
        int key = Key(r);
        lock (Pending) if (!Pending.ContainsKey(key) && !Cache.ContainsKey(key)) Pending[key] = Task.Run(() => PoundRingTexture.Render(r));
    }

    /// <summary>The texture for a radius, or null while its pixels are still being made (or for a radius that is no number above 0); main thread. With <paramref name="wait"/> (test views) it waits for them.</summary>
    public static ImageTexture? Texture(float radius, bool wait = false)
    {
        float r = PoundRingTexture.ClampRadius(radius);
        if (r <= 0f) return null;
        int key = Key(r);
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Request(r);
        Task<byte[]> task;
        lock (Pending) task = Pending[key];
        if (wait) task.Wait();
        if (!task.IsCompletedSuccessfully) return null;
        var image = Image.CreateFromData(PoundRingTexture.Pixels, PoundRingTexture.Pixels, false, Image.Format.Rgba8, task.Result);
        image.GenerateMipmaps();
        lock (Pending) Pending.Remove(key);
        return Cache[key] = ImageTexture.CreateFromImage(image);
    }
}
