using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Extras.Warfront.Dress;

namespace SmClient;

/// <summary>
/// The dressing's install props (dressing second pass; review 1 item 2: "the Ork props, banners and totems the project's loaders can already draw from the install"). The loaders in the project are <see cref="MekModels"/> and <see cref="TfwBanners"/>, which read
/// the original's static models at run time (read only; nothing is copied or kept): the Ork totem <c>waaaghbanner</c>, the Space Marine banner <c>sm_banner</c>, the scrap heap <c>ork_junkpile_d_healthy</c>, the Ork turret <c>ork_turret_healthy</c>, the tesla tower
/// <c>ork_elektrik_tower_healthy</c>, the spike barrier <c>Spikes_A</c> and the rok <c>ork_rok</c>. Not available from the install by these loaders (cloth and banner types not decoded: docs/discovery.md): <c>ork_flag_a</c>, <c>ork_ribbon_*</c>, <c>banner_mech*</c>.
/// So the drawn skull boards (glyph boards on the street blocks and the shell, the aquila board at the HQ's gate) stay, and are labelled placeholders: no install model carries a skull board. When the install or a model is missing the plan's fallback pieces draw instead.
/// One <see cref="Start"/>, then <see cref="Poll"/> a frame at a time until it is done (one model is built a frame), then <see cref="Place"/>.
/// </summary>
public sealed class WarfrontDressProps
{
    static readonly Dictionary<string, MekModels.Part> Parts = new()
    {
        [DressPropKeys.Waaagh] = TfwBanners.OrkBanner, [DressPropKeys.SmBanner] = TfwBanners.ImperialBanner, [DressPropKeys.Junkpile] = MekModels.Trap,
        [DressPropKeys.Turret] = MekModels.Turret, [DressPropKeys.TurretWreck] = MekModels.TurretWreck, [DressPropKeys.Tower] = MekModels.ShieldGenerator,
        [DressPropKeys.Spikes] = MekModels.Spikes, [DressPropKeys.Rok] = MekModels.Rok,
    };

    MekModels? _models;
    public bool Done => _models is null || _models.Done;
    int _placed, _fallbackKeys;
    long _startMs;

    /// <summary>The install's folder (found as the client finds it; null when there is none): never read from the repository's local config by this slice.</summary>
    public static string? GameDir()
    {
        try { return SmContent.GameInstall.TryLocate()?.Dir; }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException)) { return null; }
    }

    /// <summary>Starts reading the models <paramref name="plan"/> needs on a worker (nothing when <paramref name="gameDir"/> is null or missing).</summary>
    public void Start(string? gameDir, DressPlan plan)
    {
        _startMs = (long)Time.GetTicksMsec();
        var keys = plan.Props.Select(p => p.Key).Distinct().Where(Parts.ContainsKey).ToList();
        if (keys.Count == 0 || string.IsNullOrEmpty(gameDir) || !System.IO.Directory.Exists(gameDir)) { GD.Print("warfront dress: no install props (no game folder): the fallback pieces draw"); return; }
        _models = new MekModels(gameDir, keys.Select(k => Parts[k]).Distinct().ToList());
    }

    /// <summary>One poll: builds at most one model; true when every model is built or given up.</summary>
    public bool Poll()
    {
        if (_models is null) return true;
        _models.Step();
        foreach (var note in _models.Notes) GD.Print("warfront dress: " + note);
        _models.Notes.Clear();
        return _models.Done;
    }

    /// <summary>Places every prop whose model built (the models' meshes are shared) and draws the plan's fallback pieces for the keys that did not; returns the triangles the fallbacks drew.</summary>
    public int Place(Node3D root, DressPlan plan)
    {
        var holder = new Node3D { Name = "InstallProps" };
        root.AddChild(holder);
        int tris = 0;
        foreach (var group in plan.Props.GroupBy(p => p.Key))
        {
            MekModels.Built? built = Parts.TryGetValue(group.Key, out var part) ? _models?.Get(part) : null;
            if (built is null)
            {
                _fallbackKeys++;
                if (plan.Fallbacks.TryGetValue(group.Key, out var pieces)) tris += WarfrontDressBake.Attach(holder, pieces, "Fallback" + group.Key, shadows: true);
                continue;
            }
            float shared = 0f;
            foreach (var p in group)
            {
                var node = MekModels.Place(built, Vector3.One * MekModels.ToHeight(built, p.Height), foot: true, group.Key + _placed);
                var turn = new Node3D { Position = new Vector3(p.Base.X, p.Base.Y, p.Base.Z), Basis = new Basis(Vector3.Back, p.Yaw) };
                turn.AddChild(node);
                holder.AddChild(turn);
                _placed++; shared += p.Height;
            }
        }
        GD.Print($"warfront dress: install props placed: {_placed} of {plan.Props.Count} (original models read from the install), {_fallbackKeys} model kinds drawn as fallbacks, {((long)Time.GetTicksMsec() - _startMs)} ms since the read began");
        return tris;
    }
}
