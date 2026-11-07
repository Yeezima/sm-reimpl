using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The Librarian on the client (Musou Mode only, the war and the 5-wave run alike; tag 16, <see cref="LibrarianWire"/>; docs/musou-expansion.md "Librarian"). The server decides everything: this draws it.
/// <list type="bullet">
/// <item>Chain lightning: each section that brings links draws them as procedural bolts (<see cref="LibrarianBolts"/>, all 40 links since the wire's 3-byte jumps).</item>
/// <item>Warp Pull and Psychic Shockwave: played when the caster's cast count changes. The Pull is the Weirdboy's summons ring at the Librarian's feet, the Shockwave the charged plasma cannon's blast with the
/// Weirdboy's shield-break, recoloured (the effects are original, their use and colour ours).</item>
/// <item>Sounds (<see cref="LibrarianSounds"/>, original events): the crackle loop while the lightning is held and its release tail, a zap per newly chained target (at most 3 a section, 8 a second in
/// all: <see cref="LibrarianZapBudget"/>), the Pull's and the Shockwave's casts, the overheat vent when the stress locks every power.</item>
/// <item>The gauge (<see cref="LibrarianHud"/>) for the own Librarian, in the weapon box's place and style (he carries no gun).</item>
/// </list>
/// Test aid <c>--tfw-librarian</c>: once in the Stormbird and alive, the client takes the Librarian's class itself (the Esc menu row's own path, <see cref="PickLibrarian"/>).
/// </summary>
public partial class NetworkView
{
    static readonly Color PsychicBlue = new(0.55f, 0.75f, 1f);
    LibrarianHud? _librarianHud;
    IReadOnlyList<LibrarianView>? _librarianShown;
    readonly Dictionary<ushort, (byte Pull, byte Shockwave)> _librarianCasts = new();

    /// <summary>What the client last heard of a Librarian for his sounds.</summary>
    sealed class LibrarianSoundState
    {
        public readonly SoundEmitter Emitter = new() { Label = "librarian" };
        public bool Casting, Locked;
        public IReadOnlyList<LibrarianLink> Links = [];
        public bool Seen;
    }

    readonly Dictionary<ushort, LibrarianSoundState> _librarianSounds = new();
    readonly LibrarianZapBudget _librarianZaps = new();
    readonly List<ushort> _librarianGone = new();
    int _librarianZapsPlayed;
    bool _librarianPicked;
    bool LibrarianTestPick => _librarianTestPick ??= CmdFlag("tfw-librarian");
    bool? _librarianTestPick;

    void TfwLibrarianFrame(PlayerState me, IReadOnlyList<PlayerState> others)
    {
        LibrarianDemoFrame();   // NetworkView.LibrarianDemo.cs: the capture aid, nothing without --librarian-demo
        LibrarianDemoAimFrame();
        if (LibrarianTestPick && !_librarianPicked && _connectedFor > 3.0 && LibrarianOffered(me, out _)) _librarianPicked = PickLibrarian("--tfw-librarian");
        var views = _tfwState.Librarians;
        ushort mine = (ushort)(_client?.PlayerId ?? 0);
        bool fresh = !ReferenceEquals(views, _librarianShown);
        _librarianShown = views;
        LibrarianView? own = null;
        double now = Time.GetTicksMsec() / 1000.0;
        foreach (var s in _librarianSounds.Values) s.Seen = false;
        foreach (var v in views)
        {
            if (v.Player == mine) own = v;
            var at = v.Player == mine ? me : FindPlayer(others, v.Player);
            LibrarianSoundFrame(v, at, v.Player == mine, fresh, now);
            bool known = _librarianCasts.TryGetValue(v.Player, out var casts);
            _librarianCasts[v.Player] = (v.PullCount, v.ShockwaveCount);
            if (!fresh || !known) continue;   // a section already shown, or the first one this client hears (no replay of old casts)
            if (v.PullCount != casts.Pull && at is { } puller) LibrarianPullFx(puller);
            if (v.ShockwaveCount != casts.Shockwave && at is { } shocker) LibrarianShockwaveFx(shocker);
            if (v.Casting) LibrarianBoltsFeed(v);
        }
        LibrarianSoundsGone();
        if (Lean || _hudView is null) return;
        if (_librarianHud is null) { _librarianHud = new LibrarianHud(); _hudView.AddChild(_librarianHud); }
        _librarianHud.Feed(own, now);
    }

    static PlayerState? FindPlayer(IReadOnlyList<PlayerState> players, ushort id)
    {
        foreach (var p in players) if (p.PlayerId == id) return p;
        return null;
    }

    readonly Dictionary<ushort, LibrarianBolts> _librarianBolts = new();
    readonly (Vector3 From, Vector3 To)[] _librarianLinks = new (Vector3, Vector3)[LibrarianBolts.MaxLinks];

    /// <summary>The links of a casting Librarian into his procedural bolts (<see cref="LibrarianBolts"/>, in the game space so the links keep the sim's axes).</summary>
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
    }

    /// <summary>
    /// A Librarian's sounds (<see cref="LibrarianSounds"/>): his emitter follows him every frame; a new section starts or ends the crackle, zaps its new targets within the budget and plays the lock's
    /// vent. The casts' sounds play with their effects (<see cref="LibrarianPullFx"/>, <see cref="LibrarianShockwaveFx"/>).
    /// </summary>
    void LibrarianSoundFrame(in LibrarianView v, PlayerState? at, bool own, bool fresh, double now)
    {
        if (!_librarianSounds.TryGetValue(v.Player, out var s)) _librarianSounds[v.Player] = s = new LibrarianSoundState { Emitter = { Own = own, Label = $"librarian {v.Player}" } };
        s.Seen = true;
        if (at is { } p) s.Emitter.Position = ToGodot(p.Position) + Vector3.Up * 1.4f;
        if (!fresh || _sound is null || Lean) return;
        if (v.Casting && !s.Casting) _sound.Post(LibrarianSounds.Loop, s.Emitter);
        else if (!v.Casting && s.Casting) _sound.Post(LibrarianSounds.Tail, s.Emitter);
        if (v.Locked && !s.Locked) _sound.Post(LibrarianSounds.Locked, s.Emitter);
        if (v.Casting)
        {
            Span<int> hits = stackalloc int[LibrarianSounds.ZapsPerSection];
            int n = LibrarianSounds.NewHits(s.Casting ? s.Links : [], v.Links, hits, LibrarianSounds.ZapsPerSection);
            for (int k = 0; k < n; k++)
            {
                if (!_librarianZaps.Take(now)) break;
                var to = v.Links[hits[k]].To;
                _sound.Post(LibrarianSounds.Zap, new SoundEmitter { Position = ToGodot(to), OneShot = true, Label = "librarian zap" });
                _librarianZapsPlayed++;
            }
        }
        s.Casting = v.Casting;
        s.Locked = v.Locked;
        s.Links = v.Links;
    }

    /// <summary>A Librarian the sections no longer name (dead, gone, another class): his crackle ends with its tail.</summary>
    void LibrarianSoundsGone()
    {
        _librarianGone.Clear();
        foreach (var (id, s) in _librarianSounds) if (!s.Seen) _librarianGone.Add(id);
        foreach (var id in _librarianGone)
        {
            if (_librarianSounds[id].Casting && _sound is not null) _sound.Post(LibrarianSounds.Tail, _librarianSounds[id].Emitter);
            _librarianSounds.Remove(id);
            _librarianCasts.Remove(id);
        }
    }

    SoundEmitter? LibrarianEmitter(PlayerState p) => _librarianSounds.TryGetValue((ushort)p.PlayerId, out var s) ? s.Emitter : null;

    void LibrarianPullFx(PlayerState p)
    {
        if (_sound is not null && LibrarianEmitter(p) is { } e) _sound.Post(LibrarianSounds.Pull, e);
        if (_fx is null || !FxActive) return;
        _fx.Play("weirdboy_summon_staff", ToGodot(p.Position), Basis.Identity, 1.5f, priority: 2, tint: PsychicBlue);
    }

    void LibrarianShockwaveFx(PlayerState p)
    {
        if (_sound is not null && LibrarianEmitter(p) is { } e) _sound.Post(LibrarianSounds.Shockwave, e);
        if (_fx is null || !FxActive) return;
        var at = ToGodot(p.Position + new Num.Vector3(0f, 0f, 1f));
        _fx.Play("plasma_gun_mp_overcharged_dmg", at, Basis.Identity, 1.2f, priority: 2, tint: PsychicBlue);   // the 15 m blast plays at 1.5; his is 9 m
        _fx.Play("weirdboy_shield_break", at, Basis.Identity, 3f, priority: 2, tint: PsychicBlue);
    }
}

/// <summary>
/// The Librarian's gauge (Musou Mode only; <see cref="LibrarianHudModel"/>), drawn for the own Librarian in the weapon box's place, size and style (he carries no gun, so the match HUD leaves the box empty):
/// the box's dark chamfered panel and gold line turned 2.5 degrees, the stress in the reserve's large digits, PSYCHIC STRESS beside them, the stress as the heat bar along the bottom (the heat bar's red,
/// its dark track, blinking as an overheated weapon while every power is locked, then PSYCHIC OVERLOAD), and the two powers as the grenade box's cells to its left: WARP PULL (grenade key) and SHOCKWAVE
/// (reload key), each with a draining bar for its cooldown. Colours are the match HUD's (Hud.cs); layout ours, waiting for the owner's screenshot approval.
/// </summary>
public sealed partial class LibrarianHud : Control
{
    const float RefHeight = 1080f, BoxW = 388f, BoxH = 122f, Tilt = 2.5f;
    static readonly Color PanelFill = new(0.02f, 0.02f, 0.025f, 0.84f);
    static readonly Color FrameGold = new(0.74f, 0.64f, 0.32f, 0.95f);
    static readonly Color DimGold = new(0.45f, 0.4f, 0.2f, 0.9f);
    static readonly Color RedFill = new(0.86f, 0.04f, 0.04f);
    static readonly Color RedText = new(0.9f, 0.08f, 0.07f);
    static readonly Color Track = new(0.25f, 0.02f, 0.02f, 0.55f);
    static readonly Color DigitWhite = new(0.93f, 0.93f, 0.95f);
    static readonly Color Grey = new(0.62f, 0.62f, 0.64f);
    static readonly Color Gold = new(0.98f, 0.8f, 0.12f);
    static readonly Color Psychic = new(0.62f, 0.8f, 1f);
    FontVariation? _font, _fontBold;
    LibrarianView? _view;
    double _time;

    public LibrarianHud()
    {
        Name = "LibrarianHud";
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public override void _Ready() => (_font, _fontBold) = TfwHud.MakeFonts();

    /// <summary>This frame's view of the own Librarian (null when the client is not one) and the time for the blink.</summary>
    public void Feed(LibrarianView? view, double time)
    {
        _view = view;
        _time = time;
        Visible = view is not null;
        QueueRedraw();
    }

    static Vector2[] Chamfered(float x, float y, float w, float h, float cut) =>
        [new(x, y), new(x + w - cut, y), new(x + w, y + cut), new(x + w, y + h), new(x + cut, y + h), new(x, y + h - cut)];

    void Panel(float x, float w, float cut, bool lit)
    {
        var pts = Chamfered(x, 0f, w, BoxH, cut);
        DrawPolygon(pts, [PanelFill, PanelFill, new Color(0f, 0f, 0f, 0.86f), new Color(0f, 0f, 0f, 0.9f), new Color(0f, 0f, 0f, 0.9f), PanelFill]);
        DrawPolyline([.. pts, pts[0]], lit ? FrameGold : DimGold, 2f, true);
    }

    void Txt(string s, float x, float y, int size, Color c, int align = -1, bool bold = false) => TfwHud.Text(this, bold ? _fontBold : _font, s, x, y, size, c, align, 3);

    public override void _Draw()
    {
        if (_view is not { } v || _font is null) return;
        float scale = Size.Y / RefHeight;
        if (scale <= 0f) return;
        var vs = Size / scale;
        DrawSetTransform(new Vector2(vs.X - 514f, vs.Y - 248f) * scale, Mathf.DegToRad(Tilt), new Vector2(scale, scale));

        // The box: the stress in the reserve's digits, the label, the bar along the bottom.
        Panel(0f, BoxW, 11f, true);
        float fill = LibrarianHudModel.StressFill(v.Stress), blink = LibrarianHudModel.Blink(v.Locked, _time);
        Txt(((int)MathF.Ceiling(v.Stress)).ToString(System.Globalization.CultureInfo.InvariantCulture), 16f, 46f, 42, v.Locked ? RedText : DigitWhite, -1, true);
        Txt(v.Locked ? "PSYCHIC OVERLOAD" : "PSYCHIC STRESS", BoxW - 18f, 40f, 20, v.Locked ? new Color(1f, 0.3f, 0.25f, blink) : Psychic, 1, true);
        Txt(v.Locked ? "powers locked until the stress is gone" : v.Casting ? "chain lightning" : "hold fire: chain lightning", BoxW - 18f, 66f, 15, Grey, 1);
        DrawRect(new Rect2(16f, 99f, 334f, 10f), Track);
        DrawRect(new Rect2(16f, 98f, 334f * fill, 12f), v.Locked ? new Color(1f, blink * 0.35f, blink * 0.3f) : RedFill);

        // The two powers: cells left of the box, as the grenade box.
        Cell(-86f - 92f, "PULL", "GRENADE", v.PullCooldown, LibrarianRules.PullCooldown, v.Locked);
        Cell(-86f, "WAVE", "RELOAD", v.ShockwaveCooldown, LibrarianRules.ShockwaveCooldown, v.Locked);
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One);
    }

    void Cell(float x, string name, string key, float left, float cooldown, bool locked)
    {
        const float w = 80f;
        bool ready = left <= 0f && !locked;
        Panel(x, w, 9f, ready);
        Txt(name, x + w / 2f, 30f, 17, ready ? Gold : Grey, 0, true);
        if (locked) Txt("LOCKED", x + w / 2f, 64f, 15, RedText, 0, true);
        else if (left > 0f) Txt(left.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), x + w / 2f, 66f, 26, DigitWhite, 0, true);
        else Txt("READY", x + w / 2f, 64f, 16, Psychic, 0, true);
        Txt(key, x + w / 2f, 90f, 12, Grey, 0);
        float f = LibrarianHudModel.CooldownFill(left, cooldown);
        DrawRect(new Rect2(x + 8f, 100f, w - 16f, 8f), Track);
        DrawRect(new Rect2(x + 8f, 100f, (w - 16f) * f, 8f), locked ? new Color(0.5f, 0.1f, 0.1f) : ready ? Psychic : new Color(0.75f, 0.75f, 0.78f));
    }
}
