using System;
using Godot;
using SmNet;
using SmNet.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Musou Mode's melee slash and the own melee hit's small camera shake (owner, 2026-11-08; <see cref="MusouSlashRules"/>). Why the dark gore's slash (M10 item 22) was gone: since the original Ork
/// blood replaced the dark gore (2026-11-07) <c>MusouGoreFrame</c> returns before the hit events and <c>GoreView()</c> is null, so no slash was drawn by default. This one is separate from the gore:
/// it draws whatever the gore setting, from every melee hit (marker 3) by a player or a retainer. Switches: <c>--no-musou-slash</c> (none), <c>--musou-melee-shake f</c> (the shake times f, 0 = off);
/// test aid <c>--musou-slash-weapon wargear</c> (every slash is drawn as that melee wargear's, for screenshots with the scripted feed).
/// </summary>
public partial class NetworkView
{
    static readonly bool NoMusouSlash = CmdFlag("no-musou-slash");
    static readonly string? MusouSlashWeaponArg = CmdArg("musou-slash-weapon");
    static readonly float MusouMeleeShakeScale = CmdFloat("musou-melee-shake") is var shake && float.IsFinite(shake) && shake >= 0f ? shake : 1f;

    MusouSlashView? _musouSlashView;
    readonly RandomNumberGenerator _musouSlashRng = new();
    int _musouSlashFrameCount, _musouSlashes;
    ulong _musouSlashFrame;
    double _musouShakeLastHit = double.NegativeInfinity;
    int _musouShakeHits;
    float _musouShakeSum;

    /// <summary>
    /// A melee hit (marker 3) on the enemy at <paramref name="at"/> (game space) by <paramref name="e"/>'s player: the slash in the weapon's colour (within the frame's budget and the distance), and,
    /// for the own player's hit, the small shake (capped for the whole swing).
    /// </summary>
    void MusouMeleeSlash(in MusouEvent e, Num.Vector3 at, Num.Vector3 camera, bool own, PlayerState me)
    {
        if (e.Player == 0 || _gameSpace is null || _camera is null) return;
        if (own) MusouMeleeShake();
        if (NoMusouSlash || Num.Vector3.Distance(at, camera) > MusouSlashRules.Metres) return;
        PlayerState? attacker = own ? me : MusouPlayerState(e.Player);
        string? wargear = MusouSlashWeaponArg ?? (attacker is { } who ? MeleeWargear(who) : null);   // a retainer (no state): the chainsword's red
        MusouSlashAt(at, attacker?.Position, MusouSlashRules.ColourOf(wargear), MusouOldContact ? MusouContactRules.DefaultSlash : MusouContactRules.SlashOf(wargear));
    }

    /// <summary>One slash across the enemy whose chest is at <paramref name="at"/> (game space), struck from <paramref name="from"/> (the camera when unknown), within the frame's budget.</summary>
    void MusouSlashAt(Num.Vector3 at, Num.Vector3? from, MusouSlashColour colour, MusouSlashStyle style)
    {
        if (_camera is null) return;
        ulong frame = Engine.GetProcessFrames();
        if (frame != _musouSlashFrame) { _musouSlashFrame = frame; _musouSlashFrameCount = 0; }
        if (_musouSlashFrameCount >= MusouSlashRules.PerFrame) return;
        _musouSlashFrameCount++; _musouSlashes++;
        if (_musouSlashView is null)
        {
            _musouSlashView = new MusouSlashView();
            AddChild(_musouSlashView);
        }
        var origin = from is { } f ? ToGodot(f) : _camera.GlobalPosition;
        _musouSlashView.Add(ToGodot(at), origin, _camera.GlobalPosition, colour, _musouSlashRng, style);
    }

    /// <summary>The own melee hit's shake: a small one for the first enemy of a swing, less for each more, the whole swing capped (<see cref="MusouSlashRules.ShakeCap"/>).</summary>
    void MusouMeleeShake()
    {
        if (_connectedFor - _musouShakeLastHit > MusouSlashRules.ShakeSwingGap) { _musouShakeHits = 0; _musouShakeSum = 0f; }
        _musouShakeLastHit = _connectedFor;
        float amount = MusouSlashRules.Shake(_musouShakeHits, _musouShakeSum, MusouMeleeShakeScale);
        _musouShakeHits++;
        if (amount <= 0f) return;
        _musouShakeSum += amount;
        FxShake(amount);
    }
}
