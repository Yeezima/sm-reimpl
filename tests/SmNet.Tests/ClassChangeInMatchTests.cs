using SmClient.Looks;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// fix/class-change-in-match (the owner's playtest, 2026-10-28: "when i set my active class to devastator/havoc in the loadout selection screen nothing happens. i still
/// spawn as a assault marine"). The cause was the client: in a match SET ACTIVE only marked the profile's active loadout (DEPLOY was the only button that sent a change).
/// These tests pin the path a class change takes once sent (a Setup with another class, across roles, applied at the respawn and seen by the own client), a class of the
/// other side played as the side's class of the same role and said so, and the picker's rules that now send on SET ACTIVE and on closing. Synthetic data only.
/// </summary>
public class ClassChangeInMatchTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = CustomFixture.Config;

    /// <summary>B (client index 1) aims at A's server position and fires every other tick.</summary>
    static Func<int, uint, PlayerInput> BShootsA(Harness h, GameClient a, GameClient b) => (client, tick) =>
    {
        if (client != 1 || !h.Server.World.Players.TryGetValue(a.PlayerId, out var target)) return new PlayerInput(tick, 0, 0, 0, InputButtons.None);
        var d = target.Position - b.Predicted.Position;
        return new PlayerInput(tick, 0, 0, MathF.Atan2(-d.X, d.Y), tick % 2 == 0 ? InputButtons.Fire : InputButtons.None);
    };

    static (Harness H, GameClient A, GameClient B, List<string> Log) Match(int classA, int seed)
    {
        var h = new Harness(CustomFixture.Lossy, seed, clients: 0, Config, CustomFixture.Options);
        var log = new List<string>();
        h.Server.Log += log.Add;
        var a = h.AddClient(options: new ClientOptions { RequestedClass = (byte)classA }, nonce: 1);
        var b = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.CsmTac }, nonce: 2);
        Assert.True(CustomFixture.RunUntil(h, () => a.Status == ClientStatus.Connected && b.Status == ClientStatus.Connected && a.Scoreboard is not null, TimeSpan.FromSeconds(5)));
        return (h, a, b, log);
    }

    [Fact]
    public void A_class_change_across_roles_applies_at_the_respawn_and_the_own_client_plays_it()
    {
        // The owner's case: joined as Assault (--class assault), changes to another role's class while alive, dies, respawns as that class.
        var (h, a, b, log) = Match(CustomFixture.SmAssault, seed: 21);
        Assert.Equal(CustomFixture.SmAssault, h.Server.World.Players[a.PlayerId].Class);
        a.ChangeSetup(new LoadoutChoice(Class: CustomFixture.SmTac), AppearanceCode.Default, AppearanceCode.Default);
        Assert.True(CustomFixture.RunUntil(h, () => a.LastSetupAck is not null, TimeSpan.FromSeconds(5)));
        Assert.Equal(CustomFixture.SmTac, a.LastSetupAck!.Loadout.Class);
        Assert.Equal(SetupFlags.Pending, a.LastSetupAck.Flags);
        Assert.Equal(CustomFixture.SmAssault, h.Server.World.Players[a.PlayerId].Class);   // never mid-life
        Assert.Contains(log, l => l.Contains($"player {a.PlayerId} setup 1: next life 'sm - tactical'") && l.Contains("now 'sm - assault marine'"));

        Assert.True(CustomFixture.RunUntil(h, () => h.Server.World.Players[a.PlayerId] is { Alive: true, Class: CustomFixture.SmTac }, TimeSpan.FromSeconds(10), BShootsA(h, a, b)));
        var after = h.Server.World.Players[a.PlayerId];
        Assert.Equal(CustomFixture.Rifle, after.Primary);   // the new class's default loadout, not the old class's choices
        Assert.Contains(log, l => l.Contains($"player {a.PlayerId} respawned as 'sm - tactical' (setup 1 applied)"));
        // The own client predicts the new class (its weapons and movement follow it), not the class it joined with.
        Assert.True(CustomFixture.RunUntil(h, () => a.Predicted.Class == CustomFixture.SmTac, TimeSpan.FromSeconds(2)));
        // It stays: later respawns keep the changed class.
        Assert.True(CustomFixture.RunUntil(h, () => !h.Server.World.Players[a.PlayerId].Alive, TimeSpan.FromSeconds(10), BShootsA(h, a, b)));
        Assert.True(CustomFixture.RunUntil(h, () => h.Server.World.Players[a.PlayerId].Alive, TimeSpan.FromSeconds(5)));
        Assert.Equal(CustomFixture.SmTac, h.Server.World.Players[a.PlayerId].Class);
        Assert.Single(log, l => l.Contains("respawned as"));   // only the respawn that applied the setup is logged
        output.WriteLine(string.Join("\n", log));
    }

    [Fact]
    public void A_class_of_the_other_side_is_played_as_the_sides_class_of_the_same_role_and_the_player_is_told()
    {
        // A Space Marine asking for a Chaos class (Raptor, the Chaos assault class; the owner's "Havoc" for the Devastator role): the server plays the Space Marine
        // class of that role (Assault) and its ack names it, so the client can say so instead of the player finding out at the respawn.
        var (h, a, b, log) = Match(CustomFixture.SmTac, seed: 22);
        a.ChangeSetup(new LoadoutChoice(Class: CustomFixture.CsmRaptor), AppearanceCode.Default, AppearanceCode.Default);
        Assert.True(CustomFixture.RunUntil(h, () => a.LastSetupAck is not null, TimeSpan.FromSeconds(5)));
        var ack = a.LastSetupAck!;
        Assert.Equal(CustomFixture.SmAssault, ack.Loadout.Class);
        Assert.Equal(SetupFlags.Pending, ack.Flags);   // the role mapping is not a defaulted choice: the client compares the class it sent with the ack's
        Assert.Contains(log, l => l.Contains("asked for 'csm - raptor', a class of the other side"));
        var (text, problem) = PickerRules.AckText(false, 0, ack, sent: true, acceptedClass: "Assault Marine", sentClass: "Raptor", sideName: "Space Marines");
        Assert.True(problem);
        Assert.Equal("Raptor is not a class of the Space Marines: you respawn as Assault Marine, its counterpart", text);
        Assert.True(CustomFixture.RunUntil(h, () => h.Server.World.Players[a.PlayerId] is { Alive: true, Class: CustomFixture.SmAssault, Team: Team.SpaceMarines },
            TimeSpan.FromSeconds(10), BShootsA(h, a, b)));
    }

    // ---- the picker's rules (client/Looks/LookLogic.cs) ----

    [Fact]
    public void The_ack_names_the_class_the_player_respawns_as()
    {
        var ok = new SetupAck(3, new LoadoutChoice(2), SetupFlags.Pending, 0);
        Assert.Equal(("Devastator accepted: applies when you respawn", false), PickerRules.AckText(false, 0, ok, true, "Devastator", "Devastator", "Space Marines"));
        Assert.Equal(("Loadout accepted: applies when you respawn", false), PickerRules.AckText(false, 0, ok, true));   // no names known: as before
        var defaulted = ok with { Flags = SetupFlags.Pending | SetupFlags.Defaulted };
        Assert.Equal(("Devastator accepted, but some choices were not valid and use their defaults", true), PickerRules.AckText(false, 0, defaulted, true, "Devastator", "Devastator"));
    }

    [Fact]
    public void Set_active_says_which_class_the_player_respawns_as_and_explains_the_other_sides_name()
    {
        Assert.Equal("Devastator is your active loadout: you respawn as Devastator.", PickerRules.ActivatedText("Devastator", "Space Marines", "Devastator", "Space Marines"));
        // The preview shows Chaos (the role tab reads "Devastator / Havoc"): a Space Marine choosing Havoc plays Devastator.
        Assert.Equal("Havoc is a Chaos class: on the Space Marines side you respawn as Devastator (same role). Rejoin on the Chaos side to play Havoc.",
            PickerRules.ActivatedText("Devastator", "Space Marines", "Havoc", "Chaos"));
        Assert.Equal("Devastator is your active loadout: you respawn as Devastator.", PickerRules.ActivatedText("Devastator", "Space Marines", null, null));
    }

    [Fact]
    public void Closing_the_menu_sends_only_a_saved_active_loadout_that_changed()
    {
        var assault = new LoadoutKey(new LoadoutChoice(0), AppearanceCode.Default, AppearanceCode.Default);
        var devastator = new LoadoutKey(new LoadoutChoice(2), AppearanceCode.Default, AppearanceCode.Default);
        var repainted = assault with { SpaceMarine = CustomFixture.Look(3) };
        Assert.True(PickerRules.SendOnClose(deployed: false, leaving: false, connected: true, assault, devastator));    // SET ACTIVE (front-end style) + Save, then RESUME
        Assert.True(PickerRules.SendOnClose(false, false, true, assault, repainted));                                     // the active loadout's look edited and saved
        Assert.False(PickerRules.SendOnClose(false, false, true, assault, assault));                                      // opened and closed: a --class join keeps its class
        Assert.False(PickerRules.SendOnClose(deployed: true, false, true, assault, devastator));                          // DEPLOY already sent it
        Assert.False(PickerRules.SendOnClose(false, leaving: true, true, assault, devastator));                           // LEAVE MATCH
        Assert.False(PickerRules.SendOnClose(false, false, connected: false, assault, devastator));
        Assert.False(PickerRules.SendOnClose(false, false, true, null, devastator));                          // the customizer was not ready at the open
    }

    [Fact]
    public void A_set_active_whose_profile_save_failed_is_not_undone_when_the_menu_closes()
    {
        // fix/set-active-save-failure (review of PR #106): SET ACTIVE sent Devastator, but the profile could not be saved (read-only or unreadable profile, an I/O
        // error), so the saved profile still names Assault as active. Taking the sent loadout as in effect made closing the menu send Assault back.
        var savedAssault = new LoadoutKey(new LoadoutChoice(0), AppearanceCode.Default, AppearanceCode.Default);
        var sentDevastator = new LoadoutKey(new LoadoutChoice(2), AppearanceCode.Default, AppearanceCode.Default);
        Assert.True(PickerRules.SendOnClose(false, false, true, sentDevastator, savedAssault));   // the old bookkeeping: closing re-sent the old loadout

        var inEffect = PickerRules.InEffectAfterSend(sentDevastator, savedAssault, saveError: "Access to the path is denied.");
        Assert.Equal(savedAssault, inEffect);
        Assert.False(PickerRules.SendOnClose(false, false, true, inEffect, savedAssault));       // closing keeps the Devastator change
        // A later edit that does get saved still goes out on close.
        Assert.True(PickerRules.SendOnClose(false, false, true, inEffect, sentDevastator with { SpaceMarine = CustomFixture.Look(2) }));

        // Saved: the sent loadout is in effect (and equals the saved active one).
        Assert.Equal(sentDevastator, PickerRules.InEffectAfterSend(sentDevastator, savedActive: null, saveError: null));
        Assert.False(PickerRules.SendOnClose(false, false, true, sentDevastator, sentDevastator));

        Assert.Equal("Not saved in your profile (Access to the path is denied.): it applies in this match only; the profile keeps its old active loadout.",
            PickerRules.NotSavedText("Access to the path is denied."));
    }

    [Fact]
    public void A_respawn_that_changed_the_class_says_so()
    {
        Assert.Equal("You are now playing Devastator", PickerRules.RespawnText(0, 2, "Devastator"));
        Assert.Equal("", PickerRules.RespawnText(2, 2, "Devastator"));
        Assert.Equal("", PickerRules.RespawnText(-1, 2, "Devastator"));   // the first spawn
    }
}
