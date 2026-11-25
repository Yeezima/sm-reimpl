using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmContent.Extras.Warfront;
using SmFormats.Havok;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The Chaos army's looks for the Musou crowd (Chaos Warfront C3; <see cref="WarfrontChaosLooksPlan"/>): one <see cref="MusouLooks.Look"/> per kind of a Chaos battle, all from the player's install at run time.
/// <list type="bullet">
/// <item>The renegade guard (<c>renegade_guard</c> on the Guardsman's co-op rig, from the co-op set): the Traitor Guardsman with the renegade's lasgun in hand, and the Cultist, the same body recoloured
/// and unarmed with the rig's melee strike (owner 2026-11-25: "Recoloured renegade guards").</item>
/// <item>The Bloodletter on its own rig (the breach's Daemon, the Gretchin row).</item>
/// <item>The marine-rig units: a PvP Chaos class look the networked view builds (<see cref="OfferMarine"/>: the Tactical's, the Havoc's, the Raptor's, a predefined set for the Champion, the Chosen and the
/// Lord), with the co-op unit's weapon model put in the hands where the data mounts one (<see cref="WarfrontChaosLooksPlan.MarineLook"/>).</item>
/// </list>
/// Files are read on workers; <see cref="Step"/> builds the Godot objects on the main thread, one look a call. A look that cannot be had is left null: the crowd then draws the Chaos Marine for a marine
/// kind and nothing for the others, never an Ork (<see cref="MusouLooks.WarfrontOwnsLooks"/>).
/// </summary>
public sealed class WarfrontChaosLooks
{
    /// <summary>The kinds drawn on the marine rig, in the order their looks are asked for (the Tactical first: every other marine kind falls back to it).</summary>
    public static readonly MusouEnemyType[] MarineKinds =
    [
        MusouEnemyType.ChaosMarine, MusouEnemyType.Stormboy, MusouEnemyType.BigShootaNob, MusouEnemyType.RokkitBoy, MusouEnemyType.Nob,
        MusouEnemyType.BossBoy, MusouEnemyType.BossShootaBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossNob, MusouEnemyType.Warboss,
    ];

    sealed record RigRead(RigAssets Rig, CharacterPiece Body, CharacterPiece? Gun, HavokBoundAnimation?[] Clips, HavokBoundAnimation?[] Melee, string Clipped, List<string> Notes);
    sealed record GunRead(string? Wargear, CharacterPiece? Piece, string? Note);

    readonly string _gameDir;
    readonly Task<RigRead> _renegade, _bloodletter;
    readonly Dictionary<MusouEnemyType, Task<GunRead>> _guns = new();
    readonly MusouLooks.Look?[] _looks = new MusouLooks.Look?[256];
    readonly HashSet<MusouEnemyType> _marineDone = new();
    readonly Dictionary<RigAssets, PieceCache> _marinePieces = new(ReferenceEqualityComparer.Instance);
    bool _renegadeDone, _bloodletterDone;

    public List<string> Notes { get; } = new();

    /// <param name="heldExtra">Per marine kind, more wargear names to try for its hands (the PvP weapons of the match's config whose names hold a word of the plan's).</param>
    public WarfrontChaosLooks(string gameDir, Func<MusouEnemyType, IEnumerable<string>> heldExtra)
    {
        _gameDir = gameDir;
        _renegade = Task.Run(ReadRenegade);
        _bloodletter = Task.Run(ReadBloodletter);
        foreach (var kind in MarineKinds)
        {
            if (WarfrontChaosLooksPlan.MarineLook(kind) is not { Held.Count: > 0 } plan) continue;
            var names = plan.Held.Concat(heldExtra(kind)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _guns[kind] = Task.Run(() => ReadGun(names));
        }
    }

    /// <summary>The look of <paramref name="kind"/> (null: not built yet, or none to be had).</summary>
    public MusouLooks.Look? For(MusouEnemyType kind) => _looks[(byte)WarfrontChaosLooksPlan.DrawnKind(kind)];

    /// <summary>Whether <paramref name="kind"/>'s marine look has been made (or given up).</summary>
    public bool HasMarine(MusouEnemyType kind) => _marineDone.Contains(kind);

    /// <summary>Every look built so far, for the census line.</summary>
    public IEnumerable<(MusouEnemyType Kind, MusouLooks.Look Look)> Built() => Enumerable.Range(0, _looks.Length).Where(i => _looks[i] is not null).Select(i => ((MusouEnemyType)i, _looks[i]!));

    // ---------------------------------------------------------------- workers

    static RigProfile RenegadeProfile => new("chaos_renegade", WarfrontChaosLooksPlan.CoopArchive, WarfrontChaosLooksPlan.RenegadeRig, WarfrontChaosLooksPlan.RenegadeBody)
    {
        Clips = GuardRig.Clips,
        DefaultWargear = WarfrontChaosLooksPlan.RenegadeGuns,
    };
    static RigProfile BloodletterProfile => new("chaos_bloodletter", WarfrontChaosLooksPlan.CoopArchive, WarfrontChaosLooksPlan.BloodletterRig, WarfrontChaosLooksPlan.BloodletterBody);

    RigRead ReadRenegade()
    {
        var notes = new List<string>();
        var profile = RenegadeProfile;
        var rig = RigAssets.Load(_gameDir, profile, notes);
        var body = Piece(profile.ArchivePath(_gameDir), WarfrontChaosLooksPlan.RenegadeBody, rig, notes);
        CharacterPiece? gun = null;
        try
        {
            var bones = rig.Rig.Skeleton.BoneNames;
            var catalogue = WeaponModels.Load(_gameDir, profile, notes, WarfrontChaosLooksPlan.RenegadeGuns, bones);
            GuardLasgun? mount = null;
            foreach (var name in WarfrontChaosLooksPlan.RenegadeGuns)
                if (catalogue.Held(name) is { } held) { mount = GuardLasgun.From(WeaponModel.Load(_gameDir, held, notes), held.Wield!, notes); notes.Add($"chaos looks: the renegade holds {name}"); break; }
            mount ??= GuardLasgun.Load(_gameDir, bones, notes);   // the Guardsman's own lasgun on the same rig
            gun = new CharacterPiece("chaos_lasgun", mount.Model.Model, null) { Rigid = mount.Rigid, Hidden = mount.Hidden };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { notes.Add($"chaos looks: the renegade's lasgun could not be read ({ex.Message}); the Traitor Guardsman is unarmed"); }
        var armed = Clips(rig, WarfrontChaosLooksPlan.RenegadeClips(armed: true), out string a);
        var melee = Clips(rig, WarfrontChaosLooksPlan.RenegadeClips(armed: false), out string m);
        return new RigRead(rig, body, gun, armed, melee, $"armed {a}; cultist {m}", notes);
    }

    RigRead ReadBloodletter()
    {
        var notes = new List<string>();
        var profile = BloodletterProfile;
        var rig = RigAssets.Load(_gameDir, profile, notes);
        var body = Piece(profile.ArchivePath(_gameDir), WarfrontChaosLooksPlan.BloodletterBody, rig, notes);
        var clips = Clips(rig, WarfrontChaosLooksPlan.BloodletterClips, out string c);
        return new RigRead(rig, body, null, clips, clips, c, notes);
    }

    GunRead ReadGun(List<string> names)
    {
        var notes = new List<string>();
        try
        {
            var catalogue = WeaponModels.Load(_gameDir, RigProfiles.Marine, notes, names, null);
            foreach (var name in names)
            {
                if (catalogue.Held(name) is not { } held) continue;
                var mount = GuardLasgun.From(WeaponModel.Load(_gameDir, held, notes), held.Wield!, notes);
                return new GunRead(name, new CharacterPiece("chaos_held_" + name, mount.Model.Model, null) { Rigid = mount.Rigid, Hidden = mount.Hidden }, null);
            }
            return new GunRead(null, null, $"chaos looks: none of {string.Join(", ", names)} has a wield mount on the marine rig; unarmed");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new GunRead(null, null, $"chaos looks: the weapon {names.FirstOrDefault()} could not be read ({ex.Message}); unarmed"); }
    }

    /// <summary>The crowd's clip slots from the names (null slots and names the rig lacks take the run or the idle, as <c>MusouLooks.Fill</c>); <paramref name="said"/> the names found.</summary>
    static HavokBoundAnimation?[] Clips(RigAssets rig, IReadOnlyList<string?> names, out string said)
    {
        var clips = new HavokBoundAnimation?[MusouLooks.ClipCount];
        var found = new List<string>();
        for (int i = 0; i < clips.Length && i < names.Count; i++)
            if (names[i] is { } n && rig.Animation(n) is { } clip) { clips[i] = clip; found.Add(n); }
        MusouLooks.WarfrontFill(clips);
        said = $"{found.Count} of {names.Count(n => n is not null)} clips ({string.Join(", ", found)})";
        return clips;
    }

    /// <summary>A mesh's nearest level of detail: its skinned blocks on the rig, its rigid blocks under their tree bones (as the Guardsman's and the Weirdboy's).</summary>
    static CharacterPiece Piece(string archive, string name, RigAssets rig, List<string> notes)
    {
        var model = StaticModel.Load(archive, name, notes);
        var tree = ObjectTree.Load(archive, name, notes) ?? throw new System.IO.FileNotFoundException($"no object tree '{name}.O3d'");
        var drawn = tree.Visuals.Where(v => v.Renderable && v.NearLevel && v.MeshId >= 0 && v.MeshId < model.Mesh.Blocks.Count
                                            && !v.RefNode.Contains("wounded", StringComparison.OrdinalIgnoreCase)).ToList();
        var hidden = Enumerable.Range(0, model.Mesh.Blocks.Count).Where(b => drawn.All(v => v.MeshId != b)).ToHashSet();
        var rigid = new Dictionary<int, RigidBlock>();
        foreach (var v in drawn.Where(v => model.Mesh.Blocks[v.MeshId].Skin is null))
            if (tree.UnderBone(v.RefNode, rig.BoneNames.Contains) is { } placed) rigid[v.MeshId] = new RigidBlock(placed.Bone, placed.Local);
        var foreign = model.Mesh.Blocks.SelectMany(b => b.Skin?.Bones ?? []).Select(b => b.Name).Where(b => !rig.BoneNames.Contains(b)).Distinct().ToList();
        if (foreign.Count > 0) notes.Add($"chaos looks: {name} binds bones its rig lacks: {string.Join(", ", foreign)}");
        notes.Add($"chaos looks: {name}: {model.Mesh.Blocks.Count} blocks, {model.Mesh.Blocks.Count - hidden.Count} drawn, {rigid.Count} rigid");
        return new CharacterPiece("chaos_" + name, model, null) { Rigid = rigid, Hidden = hidden };
    }

    // ---------------------------------------------------------------- main thread

    /// <summary>Builds at most one of the renegade, Cultist and Bloodletter looks a call once their files are read; returns the one built, or null.</summary>
    public MusouLooks.Look? Step()
    {
        if (!_renegadeDone && _renegade.IsCompleted)
        {
            _renegadeDone = true;
            if (Read(_renegade, "the renegade guard") is { } r)
            {
                var traitor = Build(r, [r.Body, r.Gun], r.Clips, "Traitor Guardsman:" + WarfrontChaosLooksPlan.RenegadeBody, null);
                var cultist = Build(r, [r.Body], r.Melee, "Cultist:" + WarfrontChaosLooksPlan.RenegadeBody, WarfrontChaosLooksPlan.CultistTint);
                _looks[(byte)MusouEnemyType.ShootaBoy] = traitor;
                _looks[(byte)MusouEnemyType.Boy] = cultist;
                Notes.Add($"chaos looks: renegade guard ready ({r.Clipped}); Traitor Guardsman {(r.Gun is null ? "unarmed" : "with the lasgun")}, Cultist recoloured and unarmed");
                return traitor ?? cultist;
            }
        }
        if (!_bloodletterDone && _bloodletter.IsCompleted)
        {
            _bloodletterDone = true;
            if (Read(_bloodletter, "the Bloodletter") is { } b)
            {
                var look = Build(b, [b.Body], b.Clips, "Daemon:" + WarfrontChaosLooksPlan.BloodletterBody, null);
                _looks[(byte)MusouEnemyType.Gretchin] = look;
                Notes.Add($"chaos looks: Bloodletter ready ({b.Clipped})");
                return look;
            }
        }
        return null;
    }

    RigRead? Read(Task<RigRead> task, string what)
    {
        if (task.IsFaulted) { Notes.Add($"chaos looks: {what} could not be read ({task.Exception?.GetBaseException().Message}); not drawn"); return null; }
        Notes.AddRange(task.Result.Notes);
        return task.Result;
    }

    MusouLooks.Look? Build(RigRead r, CharacterPiece?[] pieces, HavokBoundAnimation?[] clips, string name, (float R, float G, float B)? tint)
    {
        try
        {
            var cache = new PieceCache(_gameDir, r.Rig, Notes);
            var list = new List<AssetPiece>();
            int textured = 0, surfaces = 0;
            foreach (var piece in pieces)
            {
                if (piece is null) continue;
                if (cache.Build(piece.Name, piece, vertexColours: true) is not { } asset) { Notes.Add($"chaos looks: {piece.Name} has nothing to draw"); continue; }
                var overrides = new Material?[asset.Surfaces.Count];
                if (tint is { } t)
                    for (int s = 0; s < overrides.Length; s++)
                        if (asset.Mesh.SurfaceGetMaterial(s) is BaseMaterial3D std && std.Duplicate() is BaseMaterial3D painted)
                        {
                            var c = std.AlbedoColor;
                            painted.AlbedoColor = new Color(c.R * t.R, c.G * t.G, c.B * t.B, c.A);
                            overrides[s] = painted;
                        }
                list.Add(new AssetPiece(piece.Name, asset.Mesh, asset.Skin, overrides));
                textured += asset.Surfaces.Count(s => s.HasDiffuse); surfaces += asset.Surfaces.Count;
            }
            if (list.Count == 0 || clips.All(c => c is null)) { Notes.Add($"chaos looks: {name}: no mesh or no clip; not drawn"); return null; }
            return new MusouLooks.Look { Name = name, Assets = new MarineAssets(r.Rig, list, textured, surfaces, cache.Notes), Scale = 1f, Clips = clips };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Notes.Add($"chaos looks: {name} could not be built ({ex.GetType().Name}: {ex.Message}); not drawn");
            return null;
        }
    }

    /// <summary>
    /// A marine kind's PvP Chaos look is built (<paramref name="assets"/>; null: it could not be had, the kind takes the Tactical's): its <see cref="MusouLooks.Look"/> at the plan's scale, the weapon in
    /// its hands once read. False while the weapon is still being read (offer it again next frame).
    /// </summary>
    public bool OfferMarine(MusouEnemyType kind, MarineAssets? assets, string lookName)
    {
        if (_marineDone.Contains(kind)) return true;
        if (WarfrontChaosLooksPlan.MarineLook(kind) is not { } plan) { _marineDone.Add(kind); return true; }
        if (_guns.TryGetValue(kind, out var gun) && !gun.IsCompleted) return false;
        _marineDone.Add(kind);
        if (assets is null)
        {
            if (kind != MusouEnemyType.ChaosMarine && _looks[(byte)MusouEnemyType.ChaosMarine] is { } tactical)
            {
                _looks[(byte)kind] = new MusouLooks.Look { Name = $"{kind}:{tactical.Name} (the Tactical's look)", Assets = tactical.Assets, Scale = plan.Scale, Clips = tactical.Clips };
                Notes.Add($"chaos looks: {kind} has no look of its own; the Tactical's x{plan.Scale:0.##}");
            }
            return true;
        }
        var drawn = assets;
        string held = "unarmed";
        if (gun is { IsCompletedSuccessfully: true, Result.Note: { } note }) Notes.Add(note);
        if (gun is { IsCompletedSuccessfully: true, Result: { Piece: not null, Wargear: not null } g })
        {
            try
            {
                if (!_marinePieces.TryGetValue(assets.Rig, out var cache)) _marinePieces[assets.Rig] = cache = new PieceCache(_gameDir, assets.Rig, Notes);
                if (cache.Build(g.Piece!.Name, g.Piece, vertexColours: true) is { } asset)
                {
                    var list = assets.Pieces.Append(new AssetPiece(g.Piece.Name, asset.Mesh, asset.Skin, new Material?[asset.Surfaces.Count])).ToList();
                    drawn = new MarineAssets(assets.Rig, list, assets.TexturedSurfaces + asset.Surfaces.Count(s => s.HasDiffuse), assets.Surfaces + asset.Surfaces.Count, assets.Notes);
                    held = g.Wargear!;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Notes.Add($"chaos looks: {kind}'s {g.Wargear} could not be built ({ex.Message}); unarmed"); }
        }
        _looks[(byte)kind] = new MusouLooks.Look { Name = $"{WarfrontChaosLooksPlan.UnitName(kind)}:{lookName}", Assets = drawn, Scale = plan.Scale, Clips = MusouLooks.WarfrontMarineClips(drawn) };
        Notes.Add($"chaos looks: {WarfrontChaosLooksPlan.UnitName(kind)} drawn as {lookName} x{plan.Scale:0.##}, {held}");
        return true;
    }
}
