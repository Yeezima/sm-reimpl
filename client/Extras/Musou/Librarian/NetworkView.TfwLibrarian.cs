using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The Librarian on the client (Musou Mode only; tag 16, <see cref="LibrarianWire"/>; docs/musou-expansion.md S5). The server decides everything: this draws it.
/// <list type="bullet">
/// <item>Chain lightning: each section that brings links draws them with the Weirdboy's own bolt look (his <c>weirdboy_staff_trail</c> flown along each link, <c>weirdboy_staff_muzzle</c> at the hand), recoloured a
/// cold blue-white (ours). *Original asset loaded*, borrowed.</item>
/// <item>Warp Pull and Psychic Shockwave: played when the caster's cast count changes. The Pull is the Weirdboy's summons ring at the Librarian's feet, the Shockwave the charged plasma cannon's blast with the
/// Weirdboy's shield-break, recoloured (all ours, *placeholder*).</item>
/// <item>The stress bar and the two cooldowns (<see cref="LibrarianHud"/>) for the own Librarian. A *placeholder* layout, not yet approved by the owner's screenshot.</item>
/// </list>
/// </summary>
public partial class NetworkView
{
    static readonly Color PsychicBlue = new(0.55f, 0.75f, 1f);
    LibrarianHud? _librarianHud;
    IReadOnlyList<LibrarianView>? _librarianShown;
    readonly Dictionary<ushort, (byte Pull, byte Shockwave)> _librarianCasts = new();

    void TfwLibrarianFrame(PlayerState me, IReadOnlyList<PlayerState> others)
    {
        var views = _tfwState.Librarians;
        ushort mine = (ushort)(_client?.PlayerId ?? 0);
        bool fresh = !ReferenceEquals(views, _librarianShown);
        _librarianShown = views;
        LibrarianView? own = null;
        foreach (var v in views)
        {
            if (v.Player == mine) own = v;
            bool known = _librarianCasts.TryGetValue(v.Player, out var casts);
            _librarianCasts[v.Player] = (v.PullCount, v.ShockwaveCount);
            if (!fresh || !known) continue;   // a section already shown, or the first one this client hears (no replay of old casts)
            var at = v.Player == mine ? me : FindPlayer(others, v.Player);
            if (v.PullCount != casts.Pull && at is { } puller) LibrarianPullFx(puller);
            if (v.ShockwaveCount != casts.Shockwave && at is { } shocker) LibrarianShockwaveFx(shocker);
            if (v.Casting) LibrarianBoltsFeed(v);
        }
        if (Lean || _hudView is null) return;
        if (_librarianHud is null) { _librarianHud = new LibrarianHud(); _hudView.AddChild(_librarianHud); }
        _librarianHud.Feed(own);
    }

    static PlayerState? FindPlayer(IReadOnlyList<PlayerState> players, ushort id)
    {
        foreach (var p in players) if (p.PlayerId == id) return p;
        return null;
    }

    readonly Dictionary<ushort, LibrarianBolts> _librarianBolts = new();
    readonly (Vector3 From, Vector3 To)[] _librarianLinks = new (Vector3, Vector3)[LibrarianBolts.MaxLinks];

    /// <summary>The links of a casting Librarian into his procedural bolts (<see cref="LibrarianBolts"/>, in the game space so the links keep the sim's axes), and the sound hook.</summary>
    void LibrarianBoltsFeed(in LibrarianView v)
    {
        if (_gameSpace is null || Lean || DisplayServer.GetName() == "headless") return;
        if (!_librarianBolts.TryGetValue(v.Player, out var bolts)) { bolts = new LibrarianBolts(); _gameSpace.AddChild(bolts); _librarianBolts[v.Player] = bolts; }
        int n = Math.Min(v.Links.Count, _librarianLinks.Length);
        for (int i = 0; i < n; i++)
        {
            var l = v.Links[i];
            _librarianLinks[i] = (new Vector3(l.From.X, l.From.Y, l.From.Z), new Vector3(l.To.X, l.To.Y, l.To.Z));
        }
        bolts.Feed(_librarianLinks.AsSpan(0, n), Time.GetTicksMsec() / 1000.0);
        LibrarianSfxHook(v);
    }

    /// <summary>
    /// PLACEHOLDER hook for the lightning's sounds (a loop while held, a zap per new chain hit with a voice limit, a release tail). No original sound is wired yet: the Weirdboy's staff bolt is bound through
    /// the enemy sound sets (<c>MusouEnemySounds</c>, Wwise events posted from a near enemy's emitter), which have no free entry for a player-side psychic cast. Nothing plays.
    /// </summary>
    void LibrarianSfxHook(in LibrarianView v) { _ = v; }

    void LibrarianPullFx(PlayerState p)
    {
        if (_fx is null || !FxActive) return;
        _fx.Play("weirdboy_summon_staff", ToGodot(p.Position), Basis.Identity, 1.5f, priority: 2, tint: PsychicBlue);
    }

    void LibrarianShockwaveFx(PlayerState p)
    {
        if (_fx is null || !FxActive) return;
        var at = ToGodot(p.Position + new Num.Vector3(0f, 0f, 1f));
        _fx.Play("plasma_gun_mp_overcharged_dmg", at, Basis.Identity, 1.2f, priority: 2, tint: PsychicBlue);   // the 15 m blast plays at 1.5; his is 9 m
        _fx.Play("weirdboy_shield_break", at, Basis.Identity, 3f, priority: 2, tint: PsychicBlue);
    }
}

/// <summary>
/// The Librarian's bar (a *placeholder* layout, not yet shown to the owner): Psychic Stress as a bar above the middle of the lower edge that turns red and reads OVERLOAD while everything is locked, and the Pull
/// and the Shockwave with their cooldowns beneath it. Drawn only for a Librarian of the own client.
/// </summary>
public sealed partial class LibrarianHud : Control
{
    const float RefHeight = 1080f;
    FontVariation? _font, _fontBold;
    LibrarianView? _view;

    public LibrarianHud()
    {
        Name = "LibrarianHud";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Ready() => (_font, _fontBold) = TfwHud.MakeFonts();

    /// <summary>This frame's view of the own Librarian; null when the client is not one.</summary>
    public void Feed(LibrarianView? view)
    {
        _view = view;
        Visible = view is not null;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_view is not { } v || _font is null) return;
        float scale = Size.Y / RefHeight;
        if (scale <= 0f) return;
        float w = 360f * scale, h = 16f * scale, x = Size.X / 2f - w / 2f, y = Size.Y * 0.84f;
        DrawRect(new Rect2(x - 2f * scale, y - 2f * scale, w + 4f * scale, h + 4f * scale), new Color(0f, 0f, 0f, 0.65f));
        var fill = v.Locked ? new Color(0.9f, 0.1f, 0.08f) : new Color(0.45f, 0.55f + 0.3f * (v.Stress / 100f), 1f - 0.5f * (v.Stress / 100f));
        DrawRect(new Rect2(x, y, w * Math.Clamp(v.Stress / 100f, 0f, 1f), h), fill);
        TfwHud.Text(this, _fontBold, v.Locked ? "PSYCHIC OVERLOAD" : "PSYCHIC STRESS", Size.X / 2f, y - 8f * scale, (int)(18f * scale), v.Locked ? new Color(1f, 0.3f, 0.25f) : new Color(0.93f, 0.93f, 0.95f), 0, 3);
        string pull = v.PullCooldown > 0f ? $"WARP PULL {v.PullCooldown:0.0}s" : "WARP PULL READY";
        string shock = v.ShockwaveCooldown > 0f ? $"SHOCKWAVE {v.ShockwaveCooldown:0.0}s" : "SHOCKWAVE READY";
        TfwHud.Text(this, _font, pull + "   (grenade key)", Size.X / 2f - 10f * scale, y + h + 26f * scale, (int)(16f * scale), v.PullCooldown > 0f || v.Locked ? new Color(0.6f, 0.6f, 0.62f) : new Color(0.8f, 0.9f, 1f), 1, 3);
        TfwHud.Text(this, _font, shock + "   (reload key)", Size.X / 2f + 10f * scale, y + h + 26f * scale, (int)(16f * scale), v.ShockwaveCooldown > 0f || v.Locked ? new Color(0.6f, 0.6f, 0.62f) : new Color(0.8f, 0.9f, 1f), -1, 3);
    }
}
