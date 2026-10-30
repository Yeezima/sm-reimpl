using SmClient.Looks;
using SmContent;
using SmContent.Customization;
using SmNet;
using SmSim;
using Xunit;

namespace SmNet.Tests;

/// <summary>
/// The networked view's engine-free look rules (customizer T7; <c>client/Looks/LookLogic.cs</c>, compiled into this assembly): model keys, when a character may swap its
/// model, the per-frame build budget, the own look's promotion at the respawn, the hitch meter and the loadout picker's texts. Synthetic data only.
/// </summary>
public class NetworkLookTests
{
    static ResolvedColour Colour(string name) => new(name, [0.1f, 0.2f, 0.3f], [0f, 0f, 0f]);

    static ResolvedAppearance Look(string head = "head_a", string primary = "Blue", bool pattern = true, string classKey = "sm - tactical", EmblemHash emblem = default, (byte, byte, byte)? lens = null)
    {
        var pieces = new List<ResolvedPiece> { new("head", head, [head + "_pkg"], false), new("torso", "torso_a", ["torso_a_pkg"], false) };
        var paint = new Dictionary<string, ResolvedPaint>
        {
            ["head"] = new(Colour(primary), Colour("Gold"), Colour("Black"), pattern),
            ["torso"] = new(Colour(primary), Colour("Gold"), null, pattern),
        };
        var right = emblem.IsNone ? ResolvedDecal.None : new ResolvedDecal(DecalKind.Emblem, null, emblem);
        return new ResolvedAppearance(CustomizationCatalog.SpaceMarine, classKey, null, pieces, paint, ResolvedDecal.None, right, lens,
            new HashSet<string>(), new HashSet<string>(), "", []);
    }

    // ---- model keys ----

    [Fact]
    public void Same_drawing_same_key_whatever_the_input()
    {
        Assert.Equal(LookKeys.Of(Look(), true), LookKeys.Of(Look(), true));
    }

    [Fact]
    public void Any_visible_difference_changes_the_key()
    {
        string baseKey = LookKeys.Of(Look(), true);
        Assert.NotEqual(baseKey, LookKeys.Of(Look(head: "head_b"), true));
        Assert.NotEqual(baseKey, LookKeys.Of(Look(primary: "Red"), true));
        Assert.NotEqual(baseKey, LookKeys.Of(Look(pattern: false), true));
        Assert.NotEqual(baseKey, LookKeys.Of(Look(classKey: "sm - assault"), true));
        Assert.NotEqual(baseKey, LookKeys.Of(Look(lens: (1, 2, 3)), true));
        Assert.NotEqual(baseKey, LookKeys.Of(Look(emblem: new EmblemHash(1, 2)), true));
    }

    [Fact]
    public void An_emblem_arriving_changes_the_key_once()
    {
        var look = Look(emblem: new EmblemHash(7, 9));
        Assert.NotEqual(LookKeys.Of(look, false), LookKeys.Of(look, true));
    }

    [Fact]
    public void Carried_key_ignores_order_case_and_side_prefix()
    {
        Assert.Equal(LookKeys.CarriedKey(["sm_mp_ranged_bolter", "mp_perk_iron_halo"]), LookKeys.CarriedKey(["MP_PERK_IRON_HALO", "csm_mp_ranged_bolter", "mp_perk_iron_halo"]));
        Assert.NotEqual(LookKeys.CarriedKey(["mp_perk_iron_halo"]), LookKeys.CarriedKey(["mp_perk_other"]));
    }

    // ---- swap rules ----

    [Theory]
    [InlineData(null, "a", false, true, false, LookAction.Create)]    // no model yet: the placeholder is made at once, nothing waits
    [InlineData(null, "a", true, false, false, LookAction.Create)]
    [InlineData("a", "a", true, true, false, LookAction.Keep)]        // already wearing it
    [InlineData("a", "b", false, true, false, LookAction.Keep)]       // not built yet: keep the current model
    [InlineData("a", "b", true, true, false, LookAction.Swap)]        // ready and alive: swap
    [InlineData("a", "b", true, false, false, LookAction.Keep)]       // dead (ragdoll or lying body): keeps its model
    [InlineData("a", "b", true, true, true, LookAction.Keep)]         // just died this frame: the ragdoll is built from this model first
    public void Swap_decisions(string? current, string wanted, bool ready, bool alive, bool deathPending, LookAction expected) =>
        Assert.Equal(expected, SwapRules.Decide(current, wanted, ready, alive, deathPending));

    [Fact]
    public void A_placeholder_is_replaced_when_the_real_look_is_ready()
    {
        Assert.Equal(LookAction.Keep, SwapRules.Decide(LookKeys.Placeholder, "real", false, true, false));
        Assert.Equal(LookAction.Swap, SwapRules.Decide(LookKeys.Placeholder, "real", true, true, false));
    }

    // ---- build budget ----

    sealed class FakeClock { public double Now; }

    [Fact]
    public void The_scheduler_stops_when_the_frame_budget_is_spent()
    {
        var clock = new FakeClock();
        var scheduler = new BuildScheduler(() => clock.Now, 5.0);
        var tried = new List<string>();
        for (int i = 0; i < 6; i++) scheduler.Want("look" + i, 1);
        var done = scheduler.Run(key => { tried.Add(key); clock.Now += 2.0; return true; });
        // 2 ms per step: after two steps 4 ms < 5, after three 6 ms >= 5.
        Assert.Equal(["look0", "look1", "look2"], tried);
        Assert.Equal(3, done.Count);
        Assert.Equal(1, scheduler.Deferred);
        Assert.Equal(3, scheduler.Pending);
    }

    [Fact]
    public void At_least_one_look_is_tried_per_frame_even_over_budget()
    {
        var clock = new FakeClock();
        var scheduler = new BuildScheduler(() => clock.Now, 1.0);
        scheduler.Want("a", 1); scheduler.Want("b", 1);
        int tried = 0;
        scheduler.Run(_ => { tried++; clock.Now += 50; return false; });
        Assert.Equal(1, tried);
    }

    [Fact]
    public void The_own_look_goes_first_then_the_first_wanted()
    {
        var scheduler = new BuildScheduler(() => 0, 100);
        scheduler.Want("other1", 1); scheduler.Want("other2", 1); scheduler.Want("own", 0);
        var order = new List<string>();
        scheduler.Run(k => { order.Add(k); return true; });
        Assert.Equal(["own", "other1", "other2"], order);
    }

    [Fact]
    public void An_unfinished_look_is_retried_and_a_forgotten_one_dropped()
    {
        var scheduler = new BuildScheduler(() => 0, 100);
        scheduler.Want("a", 1); scheduler.Want("b", 1);
        scheduler.Run(_ => false);
        Assert.Equal(2, scheduler.Pending);
        scheduler.Want("a", 1);   // b is no longer wanted (the player left or changed again)
        var tried = new List<string>();
        scheduler.Run(k => { tried.Add(k); return true; });
        Assert.Equal(["a"], tried);
        Assert.Equal(0, scheduler.Pending);
    }

    [Fact]
    public void Eight_players_changing_looks_are_spread_over_frames()
    {
        // Eight looks of ~3 ms of main-thread work each (a few pieces): with a 5 ms budget no frame spends more than budget + one step.
        var clock = new FakeClock();
        var scheduler = new BuildScheduler(() => clock.Now, 5.0);
        var pending = Enumerable.Range(0, 8).Select(i => "look" + i).ToHashSet();
        double worst = 0;
        int frames = 0;
        while (pending.Count > 0 && frames < 20)
        {
            foreach (var k in pending) scheduler.Want(k, 1);
            double before = clock.Now;
            foreach (var k in scheduler.Run(k => { clock.Now += 3.0; return true; })) pending.Remove(k);
            worst = Math.Max(worst, clock.Now - before);
            frames++;
        }
        Assert.Empty(pending);
        Assert.True(worst <= 6.0, $"a frame spent {worst} ms");
        Assert.Equal(4, frames);   // two looks per frame
    }

    // ---- emblems ----

    [Fact]
    public void Emblem_probes_are_rate_limited_and_arrivals_are_immediate()
    {
        double now = 0;
        int probes = 0;
        var have = new HashSet<EmblemHash>();
        var emblems = new EmblemAvailability(h => { probes++; return have.Contains(h); }, () => now);
        var hash = new EmblemHash(3, 4);
        Assert.True(emblems.Has(EmblemHash.None));
        Assert.False(emblems.Has(hash));
        for (int i = 0; i < 30; i++) { now += 0.01; Assert.False(emblems.Has(hash)); }
        Assert.Equal(1, probes);   // 0.3 s: still inside the half second
        now += 0.5;
        have.Add(hash);
        Assert.True(emblems.Has(hash));
        Assert.True(emblems.Has(hash));
        Assert.Equal(2, probes);   // found: never asked again
        var other = new EmblemHash(5, 6);
        emblems.Arrived(other);
        Assert.True(emblems.Has(other));
        Assert.Equal(2, probes);
    }

    // ---- the own look ----

    static AppearanceCode Code(byte head) => AppearanceCode.Default.WithPiece(ArmourSlot.Head, head);

    [Fact]
    public void A_requested_look_applies_at_the_respawn_after_the_acknowledgement()
    {
        var own = new OwnLooks(Code(1), Code(2));
        Assert.Equal(Code(1), own.Active(Team.SpaceMarines));
        Assert.Equal(Code(2), own.Active(Team.Chaos));
        own.Update(alive: true, 0);
        own.Request(Code(10), Code(11));
        Assert.False(own.Update(alive: true, 0));       // nothing changes mid-life
        Assert.Equal(Code(1), own.Active(Team.SpaceMarines));
        own.Acknowledged(5, false);
        Assert.False(own.Update(alive: true, 0));
        Assert.False(own.Update(alive: false, 0));      // dead: still the old look (the body keeps it)
        Assert.True(own.Update(alive: true, 0));        // respawn: the new one
        Assert.Equal(Code(10), own.Active(Team.SpaceMarines));
        Assert.Equal(Code(11), own.Active(Team.Chaos));
        Assert.Null(own.Pending);
        Assert.False(own.Update(alive: false, 0));
        Assert.False(own.Update(alive: true, 0));       // nothing pending: unchanged
    }

    [Fact]
    public void An_acknowledgement_in_the_frame_of_the_respawn_does_not_promote_the_look_a_life_early()
    {
        // The Setup reached the server within a round trip of the respawn: its ack and the respawn show up in the same frame. The server may have spawned the old look.
        var own = new OwnLooks(Code(1), Code(2));
        own.Update(alive: true, 0);
        own.Request(Code(10), Code(11));
        own.Update(alive: false, 0);
        own.Acknowledged(7, false);                    // this frame ...
        Assert.False(own.Update(alive: true, 0));      // ... also the respawn: not applied on the respawn alone
        Assert.Equal(Code(1), own.Active(Team.SpaceMarines));
        Assert.False(own.Update(alive: true, 6));      // the scoreboard still shows the old version: the server did not apply it
        Assert.True(own.Update(alive: true, 7));       // it did (the next scoreboard shows the acknowledged version)
        Assert.Equal(Code(10), own.Active(Team.SpaceMarines));
    }

    [Fact]
    public void Going_back_to_the_default_look_is_promoted_by_the_row_showing_version_zero()
    {
        // The server acknowledges the default look with version 0, so the version path needs the row to be present (a missing row reads 0 too).
        var own = new OwnLooks(Code(1), Code(2));
        own.Update(alive: false, 0);
        own.Request(AppearanceCode.Default, AppearanceCode.Default);
        own.Acknowledged(0, false);
        Assert.False(own.Update(alive: true, 0, rowPresent: false));   // the respawn frame: not on the respawn alone, and no row to read
        Assert.Equal(Code(1), own.Active(Team.SpaceMarines));
        Assert.False(own.Update(alive: true, 5, rowPresent: true));    // the row still shows the old version
        Assert.True(own.Update(alive: true, 0, rowPresent: true));     // the row shows 0: the server spawned the default look
        Assert.True(own.Active(Team.SpaceMarines).IsDefault);
        Assert.True(own.Active(Team.Chaos).IsDefault);
    }

    [Fact]
    public void A_default_pending_look_is_not_promoted_by_version_zero_when_the_default_is_already_shown()
    {
        var own = new OwnLooks(AppearanceCode.Default, AppearanceCode.Default);
        own.Request(AppearanceCode.Default, AppearanceCode.Default);
        own.Acknowledged(0, false);
        Assert.False(own.Update(alive: true, 0, rowPresent: true));   // nothing to switch: it clears at the next respawn
        Assert.NotNull(own.Pending);
    }

    [Fact]
    public void An_acknowledgement_a_frame_before_the_respawn_still_applies_at_it()
    {
        var own = new OwnLooks(Code(1), Code(2));
        own.Update(alive: false, 0);
        own.Request(Code(10), Code(11));
        own.Acknowledged(7, false);
        Assert.False(own.Update(alive: false, 0));     // the frame after the ack, still dead
        Assert.True(own.Update(alive: true, 0));       // the respawn
    }

    [Fact]
    public void The_most_urgent_priority_counts_when_a_look_is_wanted_twice_in_a_frame()
    {
        var scheduler = new BuildScheduler(() => 0, 100);
        scheduler.Want("shared", 1); scheduler.Want("other", 1); scheduler.Want("shared", 0);   // the own player wears the same look as a remote one
        var order = new List<string>();
        scheduler.Run(k => { order.Add(k); return true; });
        Assert.Equal(["shared", "other"], order);
        Assert.False(scheduler.IsPending("shared"));
    }

    [Fact]
    public void An_unacknowledged_request_is_not_applied_by_a_respawn()
    {
        var own = new OwnLooks(Code(1), Code(2));
        own.Request(Code(10), Code(11));
        own.Update(alive: false, 0);
        Assert.False(own.Update(alive: true, 0));
        Assert.Equal(Code(1), own.Active(Team.SpaceMarines));
        own.Acknowledged(3, false);
        // The scoreboard's own row shows the acknowledged version: the server has applied it.
        Assert.False(own.Update(alive: true, 2));
        Assert.True(own.Update(alive: true, 3));
        Assert.Equal(Code(10), own.Active(Team.SpaceMarines));
    }

    [Fact]
    public void A_newer_request_replaces_the_pending_one()
    {
        var own = new OwnLooks(Code(1), Code(2));
        own.Request(Code(10), Code(11));
        own.Acknowledged(1, false);
        own.Request(Code(20), Code(21));   // acknowledged again before it counts
        Assert.False(own.Acked);
        own.Acknowledged(2, false);
        own.Update(alive: false, 0);
        Assert.True(own.Update(alive: true, 0));
        Assert.Equal(Code(20), own.Active(Team.SpaceMarines));
    }

    [Fact]
    public void A_refused_emblem_falls_back_on_the_own_model_too()
    {
        var emblem = new EmblemHash(8, 8);
        var withEmblem = Code(1).WithDecals(AppearanceCode.DecalNone, AppearanceCode.DecalEmblem, 4).WithEmblem(emblem);
        var own = new OwnLooks(AppearanceCode.Default, AppearanceCode.Default);
        own.Request(withEmblem, withEmblem);
        own.Acknowledged(1, emblemRejected: true);
        own.Update(alive: false, 0);
        own.Update(alive: true, 0);
        Assert.False(own.Active(Team.SpaceMarines).UsesEmblem);
    }

    // ---- hitches ----

    [Fact]
    public void The_meter_blames_the_frame_after_look_work()
    {
        var meter = new HitchMeter();
        meter.Add(16, false);
        meter.Add(16, true);      // work happens in this frame ...
        meter.Add(40, false);     // ... and shows as a long frame after it
        meter.Add(60, false);     // a long frame with no look work before it
        Assert.Equal(4, meter.Frames);
        Assert.Equal(2, meter.OverLimit);
        Assert.Equal(1, meter.WorkFrames);
        Assert.Equal(1, meter.OverLimitAfterWork);
        Assert.Equal(40, meter.MaxAfterWorkMs);
        Assert.Contains("1 over 33 ms", meter.Summary());
    }

    // ---- the picker ----

    [Theory]
    [InlineData(true, true, false, false, false)]    // alive in play: C does nothing
    [InlineData(true, false, false, false, true)]    // dead
    [InlineData(true, true, true, false, true)]      // spectating
    [InlineData(true, true, false, true, true)]      // the match has ended
    [InlineData(false, false, false, false, false)]  // not connected
    public void The_picker_opens_only_when_down(bool connected, bool alive, bool spectating, bool ended, bool expected) =>
        Assert.Equal(expected, PickerRules.CanOpen(connected, alive, spectating, ended));

    [Theory]
    // connected, connectFailing, menuOpen, human -> action
    [InlineData(true, false, false, true, PickerRules.EscAction.OpenMenu)]      // in a match, alive or dead: Esc opens the menu
    [InlineData(true, false, true, true, PickerRules.EscAction.None)]           // while it is open the menu owns Esc (the creator closes it)
    [InlineData(false, true, false, true, PickerRules.EscAction.BackToMenu)]    // connecting, rejected or disconnected: the front end's way back keeps priority
    [InlineData(false, true, true, true, PickerRules.EscAction.BackToMenu)]
    [InlineData(true, true, false, true, PickerRules.EscAction.BackToMenu)]     // (a failing join wins even if a stale connected flag were set)
    [InlineData(false, false, false, true, PickerRules.EscAction.None)]         // not connected yet and not failing: nothing
    [InlineData(true, false, false, false, PickerRules.EscAction.None)]         // a bot or a replay has no menu
    public void Escape_precedence(bool connected, bool failing, bool open, bool human, PickerRules.EscAction expected) =>
        Assert.Equal(expected, PickerRules.OnEscape(connected, failing, open, human));

    [Theory]
    [InlineData(true, true, PickerRules.EscLevel.Dialog)]     // a dialog open (even over a sub-screen): the dialog goes first
    [InlineData(true, false, PickerRules.EscLevel.Dialog)]
    [InlineData(false, true, PickerRules.EscLevel.SubScreen)] // the appearance editor goes back to the creator
    [InlineData(false, false, PickerRules.EscLevel.Menu)]     // only the outermost Esc closes the menu
    public void Esc_in_the_menu_goes_back_one_level_at_a_time(bool dialog, bool subScreen, PickerRules.EscLevel expected) =>
        Assert.Equal(expected, PickerRules.EscClosesFirst(dialog, subScreen));

    [Fact]
    public void Leaving_the_match_asks_about_unsaved_edits_first()
    {
        Assert.Equal(PickerRules.LeavePrompt.SaveFirst, PickerRules.LeaveStep(unsavedEdits: true));
        Assert.Equal(PickerRules.LeavePrompt.Confirm, PickerRules.LeaveStep(unsavedEdits: false));
    }

    [Theory]
    [InlineData(true, false, true, false, true)]    // a human in a connected match closing the menu
    [InlineData(true, false, true, true, false)]    // leaving the match: the front end wants a free mouse
    [InlineData(true, false, false, false, false)]  // the connection failed
    [InlineData(true, true, true, false, false)]    // a test window never captures
    [InlineData(false, false, true, false, false)]  // a bot or a replay
    public void The_mouse_is_recaptured_only_when_the_player_is_back_in_the_match(bool human, bool testWindow, bool connected, bool leaving, bool expected) =>
        Assert.Equal(expected, PickerRules.RecaptureMouse(human, testWindow, connected, leaving));

    [Fact]
    public void The_menu_is_translucent_only_while_alive_in_a_running_match()
    {
        Assert.True(PickerRules.Translucent(alive: true, matchEnded: false));
        Assert.False(PickerRules.Translucent(alive: false, matchEnded: false));   // the death screen stays opaque
        Assert.False(PickerRules.Translucent(alive: true, matchEnded: true));     // so does the end of the match
    }

    [Fact]
    public void Esc_opens_the_menu_at_every_moment_and_there_is_no_C_key()
    {
        // The Esc menu has no alive / dead condition (CanOpen only governs a picker that was not forced by Esc).
        Assert.False(PickerRules.CanOpen(true, alive: true, spectating: false, matchEnded: false));
        Assert.True(PickerRules.CanOpen(true, alive: false, spectating: false, matchEnded: false));
        Assert.Equal(PickerRules.EscAction.OpenMenu, PickerRules.OnEscape(connected: true, connectFailing: false, menuOpen: false, human: true));   // no alive / dead input at all
    }

    [Fact]
    public void The_banner_counts_the_respawn()
    {
        Assert.Equal("Applies when you respawn - RESPAWN IN 4", PickerRules.Banner(false, 3.2f));
        Assert.Equal("Applies when you respawn - RESPAWN IN 0", PickerRules.Banner(false, 0f));
        Assert.StartsWith("Changes apply when you respawn", PickerRules.Banner(true, 3f));
        Assert.Contains("still in the match", PickerRules.Banner(true, 3f));
    }

    [Fact]
    public void The_status_line_follows_the_acknowledgement()
    {
        Assert.Equal("", PickerRules.AckText(false, 0, null, sent: false).Text);
        Assert.Equal(("Sending your loadout...", false), PickerRules.AckText(true, 1, null, true));
        Assert.True(PickerRules.AckText(true, 4, null, true).Problem);
        var ok = new SetupAck(1, LoadoutChoice.Default, SetupFlags.Pending, 3);
        Assert.Equal(("Loadout accepted: applies when you respawn", false), PickerRules.AckText(false, 0, ok, true));
        var defaulted = ok with { Flags = SetupFlags.Pending | SetupFlags.Defaulted };
        Assert.True(PickerRules.AckText(false, 0, defaulted, true).Problem);
        Assert.Contains("defaults", PickerRules.AckText(false, 0, defaulted, true).Text);
        var rejected = ok with { Flags = SetupFlags.Pending | SetupFlags.EmblemRejected };
        Assert.Contains("badge", PickerRules.AckText(false, 0, rejected, true).Text);
        Assert.Contains("uploading", PickerRules.AckText(false, 0, ok with { Flags = SetupFlags.Pending | SetupFlags.EmblemUploading }, true).Text);
    }
}
