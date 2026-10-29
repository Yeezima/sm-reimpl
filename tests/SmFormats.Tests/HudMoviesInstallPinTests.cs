using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// ui/hud-fidelity: pins what the HUD reads from the player's install (read-only; install build 7412325): every reticle the rules name is in <c>Hud.gfx</c>'s reticle container and
/// renders, the spread clips carry the labels the rules read, the weapon box has every optional display the rules hide or show, and the text fields the HUD fills exist with
/// the sizes the code was measured against. Only names, labels, counts and sizes are compared, no original bytes. Skipped when no install is found (CI).
/// </summary>
public class HudMoviesInstallPinTests
{
    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);
    static readonly Lazy<HudMovies> Movies = new(() => HudMovies.Load(Game.Value!));

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the pin needs the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void Every_reticle_the_rules_name_is_in_the_container_and_draws()
    {
        var hud = Movies.Value.Hud!;
        foreach (var spec in ReticleRules.All)
            foreach (var name in new[] { spec.Idle, spec.Active })
            {
                if (name is null) continue;
                var pic = hud.RenderAt($"{ReticleRules.Container}/{name}", ReticleRules.Container, 1.0);
                Assert.True(pic is not null, $"{name} draws nothing");
                // Every reticle is centred within 1.5 movie pixels of (0.9, 3.3) from the container's origin, the stage centre (Hud.ReticleContainer).
                Assert.InRange(pic!.Width / 2.0 - pic.OriginX, -12.0, 12.0);
            }
        // The spread clips: the bolt pistol's and the bolter's labels.
        var pistol = hud.Find($"{ReticleRules.Container}/boltpistol_mc/crosshair")!.Value.Placement.Character;
        Assert.Equal(new Dictionary<string, int> { ["min_aim"] = 0, ["min_normal"] = 2, ["max_normal"] = 24 }, hud.Labels(pistol));
        var bolter = hud.Find($"{ReticleRules.Container}/idle_mc/crosshair")!.Value.Placement.Character;
        Assert.Equal(new Dictionary<string, int> { ["min_aim"] = 0, ["min_normal"] = 1, ["max_normal"] = 14, ["max_aim"] = 24 }, hud.Labels(bolter));
        // The container sits 0.8, 3.15 movie pixels up and left of the stage centre.
        var container = hud.Find(ReticleRules.Container)!.Value.ToStage;
        Assert.Equal(639.2, container.Tx, 2); Assert.Equal(356.85, container.Ty, 2);
        // The hit marker: 10 frames.
        Assert.Equal(10, hud.FrameCount(hud.Find($"{ReticleRules.Container}/{ReticleRules.HitMarker}")!.Value.Placement.Character));
    }

    [InstallFact]
    public void The_weapon_box_has_every_display_the_rules_name()
    {
        var hud = Movies.Value.Hud!;
        const string Box = "weaponInfo_Slide/weaponInfo_container/weaponInfo_mc";
        foreach (var path in WeaponBoxRules.Optional) Assert.True(hud.Find($"{Box}/{path}") is not null, path);
        foreach (var type in new[] { "bolter", "bolt_pistol", "storm_bolter", "heavy_bolter", "plasma_gun", "plasma_pistol", "laz_cannon", "melta", "stalker", "grenade_launcher" })
            foreach (bool chaos in new[] { false, true })
            {
                var spec = WeaponBoxRules.For("", type, chaos)!;
                if (spec.Bar is { } bar) Assert.True(hud.Find($"{Box}/{bar}") is not null, $"{type}: {bar}");
                if (spec.Count is { } count) Assert.True(hud.Text($"{Box}/{count}") is not null, $"{type}: {count}");
            }
        Assert.NotNull(hud.Find($"{Box}/loadout_mc/gun_mc"));
    }

    [InstallFact]
    public void The_text_fields_the_hud_fills_have_their_sizes()
    {
        var m = Movies.Value;
        Assert.Equal(18, m.EventLog!.Text("logentry0_mc/killer_name_txt")!.Value.Text.FontHeight);
        Assert.Equal(1, m.EventLog.Text("logentry0_mc/killer_name_txt")!.Value.Text.Align);
        Assert.Equal(0, m.EventLog.Text("logentry0_mc/victim_name_txt")!.Value.Text.Align);
        Assert.Equal(20, m.Text!.Text("mp_score_mc/marine_kills")!.Value.Text.FontHeight);
        Assert.Equal(35, m.Text.Text("mp_score_mc/kill_number")!.Value.Text.FontHeight);
        Assert.Equal(24, m.Text.Text("Prompt_mc/Objective_mc/Objective_txt")!.Value.Text.FontHeight);
        Assert.Equal(42, m.Hud!.Text("score_mc/xp_event_1_mc/xp_mc/xp_mc/xp_txt")!.Value.Text.FontHeight);
        Assert.True(m.Hud.Labels(m.Hud.Find("score_mc/xp_event_1_mc/xp_icons_mc/xp_icon_1_mc/icon_mc")!.Value.Placement.Character).ContainsKey("mp_bonus_kill"));
        Assert.NotNull(m.Decorator);
    }
}
