using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Progression;

namespace SmClient.Extras.Warfront.Progression;

/// <summary>
/// Self-test of Warfront's picker and XP HUD (W8): the model, the panel, the HUD and the wire feed, driven by scripted records (no server). Headless:
/// <c>godot.cmd res://Extras/Warfront/Progression/WarfrontPickerSelfTest.tscn -- --wfpick-selftest</c>. Add <c>--wfpick-shots &lt;dir&gt;</c> and <c>--wfpick-size WxH</c> in a windowed run (<c>godot.cmd --windowed ...</c>)
/// to save a still of each state. Prints one SELFTEST PASS or FAIL per check and WFPICK SELFTEST RESULT, quits non-zero on a failure.
/// </summary>
public sealed partial class WarfrontPickerSelfTest : Node
{
    int _passed, _failed, _shot;
    string? _shotDir;
    WarfrontProgressionHost _host = null!;
    WarfrontProgressModel M => _host.Model;
    readonly List<(int Slot, int Index)> _sent = new();

    public override void _Ready() => _ = RunAsync();

    async Task Frames(int n = 3) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    /// <summary>The text audit of the UI self-test (UiSelfTest.TextAudit.cs) on this screen: no visible text over five words, none with "click" or "press", unless registered with UiKit.TextKind.</summary>
    void Audit(string screen, Node n)
    {
        var found = SmClient.FrontEnd.UiSelfTest.AuditText(n);
        Check($"text audit: {screen} has no explainer text", found.Count == 0, string.Join(" | ", found.Take(6)));
    }

    void Check(string name, bool ok, string detail = "")
    {
        if (ok) _passed++; else _failed++;
        GD.Print($"SELFTEST {(ok ? "PASS" : "FAIL")} {name}{(!ok && detail.Length > 0 ? " - " + detail : "")}");
    }

    async Task Still(string name)
    {
        await Frames(4);
        if (_shotDir is null) return;
        var img = GetViewport().GetTexture()?.GetImage();
        if (img is null || img.IsEmpty()) { GD.Print("SELFTEST NOTE no image for " + name); return; }
        string path = System.IO.Path.Combine(_shotDir, $"{++_shot:00}-{name}.png");
        img.SavePng(path);
        GD.Print("SELFTEST SHOT " + path);
    }

    static WarfrontProgressWire Progress(int level, uint extraXp, int pending, byte[] counts, int retainers = 1, int speed = 0)
    {
        uint at = (uint)WarfrontLevelXp(level), next = (uint)WarfrontLevelXp(level + 1);
        return new WarfrontProgressWire(level, at + extraXp, at, next, pending, counts, retainers, speed);
    }

    /// <summary>Total XP at a level: 1500 + 500 x (level - 1) per level (the campaign's rule; the wire carries the figures, this only builds test records).</summary>
    static long WarfrontLevelXp(int level) { long t = 0; for (int l = 1; l < level; l++) t += 1500 + 500 * (l - 1); return t; }

    static WarfrontOfferWire Offer(int index, float seconds, bool aboard, params WarfrontCard[] cards) =>
        new(true, index, cards, (int)MathF.Round(seconds * 10f), aboard);

    static readonly WarfrontCard[] WithRetainer = [WarfrontCard.Damage, WarfrontCard.CooldownReduction, WarfrontCard.AttackSpeed, WarfrontCard.MaxHp, WarfrontCard.MaxArmor, WarfrontCard.Retainer];
    static readonly WarfrontCard[] FourStats = [WarfrontCard.MaxHp, WarfrontCard.MaxArmor, WarfrontCard.Damage, WarfrontCard.CooldownReduction, WarfrontCard.AttackSpeed];

    async Task RunAsync()
    {
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--wfpick-shots");
        if (at >= 0 && at + 1 < args.Length) { _shotDir = args[at + 1]; System.IO.Directory.CreateDirectory(_shotDir); }
        UiKit.EnterVirtualSpace(GetWindow());
        int sizeAt = Array.IndexOf(args, "--wfpick-size");
        if (sizeAt >= 0 && sizeAt + 1 < args.Length)
        {
            var wh = args[sizeAt + 1].Split('x');
            GetWindow().Size = new Vector2I(int.Parse(wh[0]), int.Parse(wh[1]));
            await Frames(10);
            GD.Print($"SELFTEST SIZE window {GetWindow().Size} viewport {GetViewport().GetVisibleRect().Size}");
        }
        var backdrop = new ColorRect { Name = "Backdrop", Color = new Color(0.16f, 0.17f, 0.2f) };
        backdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(backdrop);
        _host = new WarfrontProgressionHost();
        AddChild(_host);
        _host.PickRequested += (s, i) => _sent.Add((s, i));
        await Frames(5);

        // 0. Before any Progress the HUD is hidden and the panel absent; a pick with no offer sends nothing.
        Check("scoreboard section hidden before Progress", !_host.Board.Visible && !M.HasProgress);
        Check("picker hidden before an offer", !_host.Picker.Visible);
        Check("pick with no offer sends nothing", !M.Pick(0) && _sent.Count == 0);

        // 1. HUD in battle.
        Check("section 44 handled", _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(4, 900, 0, new byte[] { 1, 0, 2, 1, 0 }, 2, 100))));
        Check("section 99 not ours", !_host.Section(99, new byte[] { 1 }));
        await Frames(2);
        Check("model holds level 4 (shown on the scoreboard only)", M.LevelText == "LEVEL 4" && !_host.Board.Visible, M.LevelText);
        _host.Board.Shown = true; await Frames(2); Check("scoreboard section shows once the board is up", _host.Board.Visible);
        Check("no +N without pending picks", M.PendingText == "");
        Check("xp text", M.XpText.Contains("XP"), M.XpText);
        Check("counters in wire order", M.CounterText(0) == "SPD\n1" && M.CounterText(2) == "DMG\n2" && M.CounterText(5) == "RET\n2");
        Check("attack speed bonus carried", MathF.Abs(M.AttackSpeedBonus - 0.1f) < 1e-4f);
        Check("no banner on the first Progress", !M.BannerShowing);
        await Still("hud-battle");

        // 2. Level up: banner for 2 s, then gone.
        int levelUps = 0; M.LevelUp += _ => levelUps++;
        _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(5, 200, 1, new byte[] { 1, 0, 2, 1, 0 }, 2, 100)));
        await Frames(2);
        Check("level rise shows LEVEL 5", M.BannerShowing && _host.Hud.BannerLabel.Visible && _host.Hud.BannerLabel.Text == "LEVEL 5" && levelUps == 1, _host.Hud.BannerLabel.Text);
        Check("pending shows +1", M.PendingText == "+1", M.PendingText);
        await Still("hud-level-up-banner");
        M.Tick(1.9f); Check("banner still up at 1.9 s", M.BannerShowing);
        M.Tick(0.2f); await Frames(2); Check("banner gone after 2 s", !M.BannerShowing && !_host.Hud.BannerLabel.Visible);
        _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(5, 300, 1, new byte[] { 1, 0, 2, 1, 0 }, 2, 100)));
        Check("same level again: no banner", !M.BannerShowing && levelUps == 1);

        // 3. An offer opens the panel: six cards, slot 6 = +1 Retainer.
        _sent.Clear();
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(5, 18.4f, false, WithRetainer)));
        await Frames(2);
        var P = _host.Picker;
        Check("offer opens the picker", P.Visible && M.OfferOpen);
        Check("header single pick", P.HeaderLabel.Text == "CHOOSE A CARD", P.HeaderLabel.Text);
        Check("slot numbers 1 to 6", Enumerable.Range(0, 6).All(i => P.SlotLabel(i).Text == (i + 1).ToString()));
        Check("card 6 is +1 RETAINER", P.NameLabel(5).Text == "RETAINER" && P.EffectLabel(5).Text.StartsWith("+1 RETAINER"), P.EffectLabel(5).Text);
        Check("effect numbers from the rules", P.EffectLabel(0).Text.StartsWith("+10 % DAMAGE\n") && P.EffectLabel(1).Text.StartsWith("-8 % ABILITY COOLDOWN\n") && P.EffectLabel(2).Text.StartsWith("+10 % ATTACK SPEED\n"), $"{P.EffectLabel(0).Text} / {P.EffectLabel(1).Text} / {P.EffectLabel(2).Text}");
        Check("effect line shows the total after the pick", P.EffectLabel(0).Text.EndsWith("NOW +20 %, THEN +30 %") && P.EffectLabel(1).Text.EndsWith("NOW -8 %, THEN -16 %") && P.EffectLabel(5).Text.EndsWith("SQUAD 3 OF 9"), $"{P.EffectLabel(0).Text} / {P.EffectLabel(1).Text} / {P.EffectLabel(3).Text}");
        Check("cooldown floor text", M.CardTotal(WarfrontCard.CooldownReduction) != "" && SmClientFloor());
        Check("owned counts", P.OwnedLabel(0).Text == "OWNED 2" && P.OwnedLabel(1).Text == "OWNED 1" && P.OwnedLabel(2).Text == "OWNED 1" && P.OwnedLabel(3).Text == "OWNED 0" && P.OwnedLabel(5).Text == "OWNED 2", string.Join("|", Enumerable.Range(0, 6).Select(i => P.OwnedLabel(i).Text)));
        Check("fixed notes (the timeout rule is a hover hint on the countdown)", P.TimeoutHint == "CARD 1 IS TAKEN WHEN THE TIME RUNS OUT" && P.ProtectedLabel.Text == "YOU CANNOT BE HARMED WHILE CHOOSING");
        await Still("picker-retainer-slot4");

        // 4. Input: the D-pad moves, up picks nothing, 1-6 pick at once, resend 0.3 s.
        Press(JoyButton.DpadRight); Press(JoyButton.DpadRight);
        Check("d-pad right x2 highlights card 3", M.Highlight == 2);
        Press(JoyButton.DpadLeft); Check("d-pad left", M.Highlight == 1);
        Press(JoyButton.DpadUp); Check("d-pad up picks nothing", _sent.Count == 0 && M.Sent < 0);
        for (int k = 0; k < 5; k++) Press(JoyButton.DpadRight); Check("highlight clamps at card 6", M.Highlight == 5);
        Press(JoyButton.DpadDown);
        Check("d-pad down confirms the highlighted card with the shown index", _sent.Count == 1 && _sent[0] == (5, 5), string.Join(",", _sent));
        Keep(5, 18f, WithRetainer); M.Tick(0.29f); Check("no resend before 0.3 s", _sent.Count == 1);
        M.Tick(0.02f); Check("resend at 0.3 s", _sent.Count == 2 && _sent[1] == (5, 5));
        M.Tick(0.3f); Keep(5, 17f, WithRetainer); M.Tick(0.3f); Check("resent every 0.3 s while open", _sent.Count == 4);
        PressKey(Key.Key2); Check("key 2 picks card 2 at once", _sent.Count == 5 && _sent[4] == (1, 5), string.Join(",", _sent));
        PressKey(Key.Key7); Check("key 7 does nothing", _sent.Count == 5);

        // 5. A new pick index replaces the offer and sends no stale pick.
        int before = _sent.Count;
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(6, 20f, false, FourStats)));
        Keep(6, 20f, FourStats); M.Tick(0.4f);
        Check("new pick index: nothing stale sent", _sent.Count == before && M.Sent < 0 && M.PickIndex == 6);
        Check("new cards shown", _host.Picker.NameLabel(0).Text == "MAX HP" && P.NameLabel(3).Text == "COOLDOWN");
        await Frames(2);
        Check("highlight reset to card 1", M.Highlight == 0);
        PressKey(Key.Key1); Check("pick after replacement carries the new index", _sent[^1] == (0, 6), _sent[^1].ToString());

        // 6. open = 0 closes; resend stops; a close for another index is ignored.
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.EncodeClosed(3));
        Check("close for a stale index ignored", M.OfferOpen);
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.EncodeClosed(6));
        await Frames(2);
        int afterClose = _sent.Count; M.Tick(1f);
        Check("open = 0 closes the picker", !P.Visible && !M.OfferOpen);
        Check("no resend after the close", _sent.Count == afterClose);
        Check("keys do nothing once closed", !M.Pick(0));

        // 7. Queue: CARD 1 OF 2, then CARD 2 OF 2 (one pick follows another, and the count can grow).
        _sent.Clear();
        _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(6, 100, 2, new byte[] { 1, 0, 2, 1, 0 }, 2, 100)));
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(7, 15f, true, WithRetainer)));
        await Frames(2);
        Check("two pending: CARD 1 OF 2", P.HeaderLabel.Text == "CARD 1 OF 2", P.HeaderLabel.Text);
        _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(6, 100, 1, new byte[] { 1, 0, 3, 1, 0 }, 2, 100)));
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.EncodeClosed(7));
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(8, 20f, true, FourStats)));
        await Frames(2);
        Check("next: CARD 2 OF 2", P.HeaderLabel.Text == "CARD 2 OF 2", P.HeaderLabel.Text);
        _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(7, 200, 2, new byte[] { 1, 0, 3, 1, 0 }, 2, 100)));
        await Frames(2);
        Check("a level earned meanwhile raises the batch: CARD 2 OF 3", P.HeaderLabel.Text == "CARD 2 OF 3", P.HeaderLabel.Text);
        await Still("picker-aboard-card-2-of-3");
        _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(7, 200, 0, new byte[] { 1, 1, 3, 1, 0 }, 2, 100)));
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.EncodeClosed(8));
        _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(7, 300, 1, new byte[] { 1, 1, 3, 1, 0 }, 2, 100)));
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(9, 20f, false, WithRetainer)));
        await Frames(2);
        Check("a later single pick starts a fresh batch", P.HeaderLabel.Text == "CHOOSE A CARD", P.HeaderLabel.Text);

        // 8. Countdown bar near zero, and the local countdown follows the clock.
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(9, 1.2f, false, WithRetainer)));
        await Frames(2);
        Check("time fraction near zero", M.TimeFraction < 0.1f && M.TimeFraction > 0f, M.TimeFraction.ToString());
        M.Tick(0.5f); Check("local countdown runs between records", MathF.Abs(M.SecondsLeft - 0.7f) < 0.01f, M.SecondsLeft.ToString());
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(9, 1.2f, false, WithRetainer)));
        await Still("picker-timeout-near-zero");

        // 9. Mouse click on a card picks it.
        _sent.Clear();
        var click = new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true };
        P.CardHit(2).EmitSignal(Control.SignalName.GuiInput, click);
        Check("click on card 3 sends slot 2 with the shown index", _sent.Count == 1 && _sent[0] == (2, 9), string.Join(",", _sent));
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.EncodeClosed(9));

        // 10. Bot: after one second pick slot (index % card count).
        var botHost = new WarfrontProgressionHost(bot: true);
        var botSent = new List<(int, int)>();
        botHost.PickRequested += (s, i) => botSent.Add((s, i));
        AddChild(botHost);
        botHost.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(10, 20f, false, WithRetainer)));
        botHost.Frame(0.9f); botHost.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(10, 19f, false, WithRetainer))); Check("bot waits one second", botSent.Count == 0);
        botHost.Frame(0.2f); Check("bot picks slot index % card count", botSent.Count == 1 && botSent[0] == (4, 10), string.Join(",", botSent));
        botHost.QueueFree();

        // 10b. A lost close: the panel closes itself after 1.0 s without an open record; a Progress with fewer pending picks closes it too; the banner waits for the close.
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(12, 20f, false, WithRetainer)));
        M.Tick(0.9f); Check("still open at 0.9 s without a record", M.OfferOpen);
        M.Tick(0.2f); await Frames(2); Check("self-closes after 1.0 s without an open record", !M.OfferOpen && !P.Visible);
        _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(8, 100, 2, new byte[] { 1, 1, 3, 1, 0 }, 2, 0)));
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(13, 20f, false, WithRetainer)));
        Check("opened with two pending", M.OfferOpen && M.Header == "CARD 1 OF 2", M.Header);
        _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(8, 100, 1, new byte[] { 1, 1, 3, 1, 0 }, 2, 0)));
        Check("pending count dropping closes it (close datagram lost)", !M.OfferOpen);
        _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(9, 100, 2, new byte[] { 1, 1, 3, 1, 0 }, 2, 0)));
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(14, 20f, false, WithRetainer)));
        Check("banner hidden while an offer is open", !M.BannerShowing && !_host.Hud.BannerLabel.Visible);
        M.Tick(0.5f); Keep(14, 19f, WithRetainer); M.Tick(0.5f);
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.EncodeClosed(14)); await Frames(2);
        Check("a banner cut short shows its remaining time after the close (the offer time did not run it down)", M.BannerShowing && _host.Hud.BannerLabel.Visible && _host.Hud.BannerLabel.Text == "LEVEL 9", _host.Hud.BannerLabel.Text);
        M.Tick(2.1f); Check("and then ends", !M.BannerShowing);

        // 11. Reset (a stage change) clears everything.
        M.Reset(); await Frames(2);
        Check("reset hides section and picker", !_host.Board.Visible && !P.Visible && !M.OfferOpen);

        // 12. Stills: HUD with pending picks and a full counter row; picker with all five stat cards and no Retainer.
        _host.Section(WarfrontProgressionWire.ProgressTag, WarfrontProgressionWire.Encode(Progress(12, 2000, 3, new byte[] { 3, 2, 4, 2, 1 }, 5, 300)));
        _host.Board.Shown = true; await Still("scoreboard-section-pending-full-counters");
        _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(11, 12.3f, false, FourStats)));
        await Still("picker-fourth-stat-card");

        FitCheck("picker", P.Labels());
        Audit("the card picker", P);

        GD.Print($"WFPICK SELFTEST RESULT passed={_passed} failed={_failed}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    void Keep(int index, float seconds, WarfrontCard[] cards) => _host.Section(WarfrontProgressionWire.OfferTag, WarfrontProgressionWire.Encode(Offer(index, seconds, false, cards)));

    /// <summary>A card with 8 cooldown cards (64 %) reads as the floor.</summary>
    static bool SmClientFloor()
    {
        var m = new WarfrontProgressModel();
        m.Apply(new WarfrontProgressWire(10, 100, 0, 200, 0, new byte[] { 0, 0, 0, 8, 0 }, 1, 0));
        return m.CardTotal(WarfrontCard.CooldownReduction) == "AT THE 40 % FLOOR";
    }

    void Press(JoyButton b) => _host.Picker._Input(new InputEventJoypadButton { ButtonIndex = b, Pressed = true });
    void PressKey(Key k) => _host.Picker._Input(new InputEventKey { PhysicalKeycode = k, Keycode = k, Pressed = true });

    void FitCheck(string where, IEnumerable<Label> labels)
    {
        foreach (var l in labels)
        {
            if (!l.Visible) continue;
            var min = l.GetCombinedMinimumSize();
            Check($"{where}: label {l.Name} fits its width", min.X <= l.Size.X + 0.5f && min.Y <= l.Size.Y + 0.5f, $"{min} in {l.Size} \"{l.Text}\"");
        }
    }

    void Overlap(string name, List<(string Name, Rect2 Rect)> rects)
    {
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
                if (rects[i].Rect.Intersects(rects[j].Rect)) { Check(name, false, $"{rects[i].Name} overlaps {rects[j].Name}"); return; }
        Check(name, true);
    }
}
