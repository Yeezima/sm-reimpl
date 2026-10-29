using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient.FrontEnd;

// ui/customizer-ork (the owner's requests of 2026-10-29): the previews' pose and turning, and the Ork Boy in the menus.
public sealed partial class UiSelfTest
{
    /// <summary>Waits until the preview has drawn its subject (at most <paramref name="frames"/> frames); returns whether it did.</summary>
    async Task<bool> WaitPreview(PreviewStage preview, int frames = 900)
    {
        for (int i = 0; i < frames && !preview.Loaded; i++) await Frames(1);
        await Frames(2);
        return preview.Loaded;
    }

    /// <summary>
    /// "Use the bp idle stance as the pose for all classes": every class on both sides stands in the bolt pistol's relaxed idle in the creator and the appearance editor.
    /// "Character spins automatically ... when choosing colors": the figure keeps its turn while nobody turns it (also while a colour is chosen), and a mouse drag still turns it.
    /// </summary>
    async Task PreviewPoseChecks()
    {
        var catalog = _services.Catalog!;
        var session = _services.Session!;
        var creator = _front.Creator!;
        var keptProfile = session.Working;
        _front.ShowCreator();
        await Frames(3);
        foreach (var race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
        {
            creator.SetPreviewRace(race);
            foreach (var role in ProfileStore.Roles(catalog))
            {
                creator.OpenSelection(Selection.DefaultFor(role));
                bool drawn = await WaitPreview(creator.Preview);
                Check($"pose: the {LoadoutRows.ClassName(catalog, role, race)} preview stands in the bolt pistol's relaxed idle", drawn && creator.Preview.IdleClip == RigClipTable.PistolRelaxedIdle,
                    $"drawn {drawn}, idle {creator.Preview.IdleClip ?? "none"}");
                // The pistol in the hand, the primary stowed (the jump-pack classes keep their melee weapon in the hand as before).
                bool jumpClass = role == "assault";
                Check($"pose: the {LoadoutRows.ClassName(catalog, role, race)} preview holds its pistol, the primary stowed",
                    creator.Preview.HeldSlot == SmSim.WeaponSlot.Pistol && (jumpClass || creator.Preview.HeldWargear?.Contains("pistol", StringComparison.OrdinalIgnoreCase) == true),
                    $"{creator.Preview.HeldSlot} {creator.Preview.HeldWargear ?? "none"}");
            }
        }
        creator.SetPreviewRace(CustomizationCatalog.SpaceMarine);

        // No turntable: the full-body framing keeps its turn for two seconds.
        creator.OpenSelection(Selection.DefaultFor(ProfileStore.FirstRole(catalog)));
        await WaitPreview(creator.Preview);
        float before = creator.Preview.Yaw;
        await Frames(120);
        Check("no auto-spin: the class creator's figure keeps its turn", Mathf.IsEqualApprox(before, creator.Preview.Yaw), $"{before:F1} -> {creator.Preview.Yaw:F1}");

        // The same while colours are chosen in the appearance editor (slot 4 is the self-test's Assault loadout).
        if (session.Working.ClassAt(4) is not null)
        {
            creator.OpenSelection(Selection.CustomSlot(4));
            await Frames(3);
            _front.ShowAppearance();
            var appearance = _front.Appearance!;
            appearance.OpenSection("colours");
            await WaitPreview(appearance.Preview);
            Check("pose: the appearance editor's figure stands in the bolt pistol's relaxed idle", appearance.Preview.IdleClip == RigClipTable.PistolRelaxedIdle, appearance.Preview.IdleClip ?? "none");
            before = appearance.Preview.Yaw;
            var swatch = FindButton(appearance, b => b is UiKit.ColourButton);
            swatch?.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(120);
            Check("no auto-spin while a colour is chosen", swatch is not null && Mathf.IsEqualApprox(before, appearance.Preview.Yaw), $"{before:F1} -> {appearance.Preview.Yaw:F1}");
            // A drag with the left button still turns the figure (half a degree per pixel).
            var at = WindowPoint(appearance.Preview);
            Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
            await Frames(2);
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
            await Frames(2);
            float beforeDrag = appearance.Preview.Yaw;
            var to = at + new Vector2(60, 0);
            Input.ParseInputEvent(new InputEventMouseMotion { Position = to, GlobalPosition = to, Relative = new Vector2(60, 0), ButtonMask = MouseButtonMask.Left });
            await Frames(2);
            Input.ParseInputEvent(new InputEventMouseButton { Position = to, GlobalPosition = to, ButtonIndex = MouseButton.Left, Pressed = false });
            await Frames(2);
            Check("a mouse drag turns the figure", appearance.Preview.Yaw - beforeDrag > 5f, $"{beforeDrag:F1} -> {appearance.Preview.Yaw:F1}");
            Click(b => b.Text == "Undo");   // the colour above
            await Frames(3);
            session.Seed(keptProfile);
            await Frames(3);
        }
        _front.ShowJoin();
        await Frames(3);
    }

    /// <summary>
    /// The Ork Boy in the menus (O9): offered in Extended (faction button, class tab, DEFAULTS chip on the Join screen), drawn on his own rig in the preview, his fixed gear
    /// and the Tactical's perks, his LOOK step instead of the appearance editor, a custom Ork loadout that saves and loads, a Join that sends his class and look; none of it
    /// in Faithful (the saved Ork loadout kept, joined as the side's default). Restores the content choice and the profile it found.
    /// </summary>
    async Task OrkChecks()
    {
        var catalog = _services.Catalog!;
        var session = _services.Session!;
        var creator = _front.Creator!;
        string originalName = _services.Settings.Content;
        var originalContent = _services.Content;
        var kept = session.Working;
        if (!ContentExtras.Compiled)
        {
            Check("ork: a build without added content never offers the Ork", !_services.OffersOrks && _services.Factions().All(f => f.Race != OrkLoadout.Race) && !_services.Roles().Contains(OrkLoadout.Role));
            return;
        }
        try
        {
            _services.SetContent(ContentExtras.All);
            int orkClass = _services.Config!.Classes.ToList().FindIndex(c => c.Race == Race.Orks);
            Check("ork: the customizer's configuration has the Ork Boy's class", orkClass >= 0 && _services.HasOrkClass, orkClass.ToString());
            Check("extended: the faction step offers the Orks", _services.Factions().Any(f => f.Race == OrkLoadout.Race) && _services.Roles().Contains(OrkLoadout.Role));

            // The Join screen's DEFAULTS row has the Ork Boy.
            _front.ShowJoin();
            await Frames(4);
            Check("extended: the Join screen offers the Ork Boy's DEFAULTS", FindButton(_front.JoinScreen!, b => b.Text.Contains("Ork Boy")) is not null);

            // The creator: faction button, class tab, DEFAULTS preview on the Ork rig in his idle.
            _front.ShowCreator();
            await Frames(4);
            Check("extended: the creator has the Orks faction and the Ork Boy tab",
                FindButton(creator, b => b.Text == "Orks") is not null && FindButton(creator, b => b.Text == "Ork Boy" && b.ToggleMode) is not null);
            creator.OpenSelection(Selection.DefaultFor(OrkLoadout.Role));
            bool drawn = await WaitPreview(creator.Preview, 1800);
            Check("ork: the preview draws the Ork Boy on his own rig", drawn && creator.Preview.ShowsOrk, $"drawn {drawn}; {creator.Preview.Description}");
            Check("ork: the preview stands in the boy's own idle", RigProfiles.OrkBoy.Clips.PreviewIdleCandidates.Contains(creator.Preview.IdleClip ?? ""), creator.Preview.IdleClip ?? "none");
            Check("ork: the Orks faction button is the pressed one", FindButton(creator, b => b.Text == "Orks")?.ButtonPressed == true && FindButton(creator, b => b.Text == "Space Marines")?.ButtonPressed == false);
            creator.OpenStep("weapons");
            await Frames(3);
            var orkWeapons = LoadoutRows.Weapons(catalog, OrkLoadout.Role, OrkLoadout.Race, null, _services.Policy);
            Check("ork: the weapons are his fixed shoota, slugga and choppa",
                new[] { "Shoota", "Slugga", "Choppa" }.All(t => FindButton(creator, b => b is UiKit.Row && b.Text == t) is not null)
                && orkWeapons.Count == 3 && orkWeapons.All(s => !s.Selectable && s.Rows.Count == 1 && s.Rows[0].Fixed && s.Rows[0].Tags.Contains("FIXED")),
                string.Join(", ", orkWeapons.SelectMany(s => s.Rows.Select(r => $"{r.Title} {string.Join("/", r.Tags)}"))));
            creator.OpenStep("perks");
            await Frames(3);
            int tacticalPerks = LoadoutRows.Perks(catalog, "tactical", CustomizationCatalog.SpaceMarine, null, _services.Policy).Count;
            int orkPerks = LoadoutRows.Perks(catalog, OrkLoadout.Role, OrkLoadout.Race, null, _services.Policy).Count;
            Check("ork: his perks are the Tactical Marine's", orkPerks == tacticalPerks && orkPerks > 0, $"{orkPerks} / {tacticalPerks}");

            // APPEARANCE on the Ork opens his LOOK step, not the armour and colour editors.
            Click(b => b.Text == "ORK LOOK...");
            await Frames(4);
            Check("ork: the look button opens his LOOK step", creator.CurrentStep == "LOOK" && creator.Visible && !_front.Appearance!.Visible, creator.CurrentStep);
            _front.ShowAppearance();
            await Frames(4);
            Check("ork: the appearance screen is replaced by his LOOK step", creator.Visible && !_front.Appearance!.Visible && creator.CurrentStep == "LOOK");
            int lookRows = OrkLoadout.LookChoices.Count(l => FindButton(creator, b => b is UiKit.Row && b.Text.StartsWith(OrkLoadout.LookTitle(l))) is not null);
            Check("ork: the LOOK step lists automatic and the five looks", lookRows == 6, lookRows.ToString());
            FindButton(creator, b => b is UiKit.Row && b.Text.StartsWith(OrkLoadout.LookTitle(1)))?.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(3);
            Check("ork: choosing a look keeps it in the profile", session.Working.OrkLook == 1, session.Working.OrkLook?.ToString() ?? "automatic");
            drawn = await WaitPreview(creator.Preview, 1800);
            Check("ork: the preview shows the chosen look", drawn && creator.Preview.Description.Contains("ork|1"), creator.Preview.Description);

            // A custom Ork loadout from an empty slot through the Orks faction button.
            int slot = LoadoutEdits.FirstEmptySlot(session.Working);
            creator.OpenSelection(Selection.CustomSlot(slot));
            await Frames(3);
            Click(b => b.Text == "Orks");
            await Frames(4);
            Check("ork: the Orks faction button makes an Ork Boy loadout in an empty slot", session.Working.ClassAt(slot)?.Role == OrkLoadout.Role, session.Working.ClassAt(slot)?.Role ?? "empty");
            creator.OpenStep("perks");
            await Frames(3);
            Click(b => b.ToggleMode && b.Text.StartsWith("Perk 2"));
            await Frames(4);
            var perkRow = FindButton(creator, b => b is UiKit.Row { IsSelected: false } && b.Disabled == false);
            perkRow?.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(3);
            Check("ork: a Tactical perk can be chosen for him", session.Working.ClassAt(slot)?.Perk(1) is not null, string.Join(",", session.Working.ClassAt(slot)?.Perks ?? []));
            Check("ork: the loadout saves", session.Save() is null);
            var reloaded = ProfileSession.Open(catalog, _services.Store);
            Check("ork: the saved Ork loadout and look load back", reloaded.Working.ClassAt(slot)?.Role == OrkLoadout.Role && reloaded.Working.OrkLook == 1
                && reloaded.Working.ClassAt(slot)?.Perk(1) == session.Working.ClassAt(slot)?.Perk(1));

            // The Join sends his class and his look.
            var plan = _services.PlanFor(Selection.CustomSlot(slot));
            Check("ork: the join plan asks for the Ork Boy's class and look", plan is not null && plan.Choice.Class == orkClass && plan.OrkLook == 1 && plan.ClassName == _services.Config!.Classes[orkClass].Name,
                $"{plan?.Choice.Class}/{plan?.OrkLook}/{plan?.ClassName}");
            Check("ork: the plan names his class for --class", plan?.ToArgs().Any(a => a.Key == "class" && a.Value == _services.Config!.Classes[orkClass].Name) == true);
            var hello = FrontEndLaunch.Apply(new SmNet.ClientOptions(), new JoinRequest("127.0.0.1:27650", plan!, _services, ContentExtras.All));
            Check("ork: the Hello carries the Ork class and look", hello.RequestedClass == orkClass && hello.OrkLook == 1 && hello.RequestedPerk2 != SmNet.Protocol.DefaultChoice,
                $"{hello.RequestedClass}/{hello.OrkLook}/{hello.RequestedPerk2}");
            var defaultsPlan = _services.PlanFor(Selection.DefaultFor(OrkLoadout.Role));
            Check("ork: the Ork DEFAULTS join as the Ork Boy", defaultsPlan?.Choice.Class == orkClass, defaultsPlan?.Choice.Class.ToString() ?? "null");
            Check("ork: --loadout default:ork names the Ork DEFAULTS", LaunchOptions.ParseLoadout("default:ork", catalog) == Selection.DefaultFor(OrkLoadout.Role));

            // Space Marines from the Ork goes back to a marine class.
            creator.OpenSelection(Selection.CustomSlot(slot));
            await Frames(3);
            Click(b => b.Text == "Space Marines");
            await Frames(4);
            if (OpenDialog() is not null) { ClickDialog("Change class"); await Frames(4); }
            Check("ork: Space Marines turns the Ork loadout back into a marine class", session.Working.ClassAt(slot)?.Role is { } back && !OrkLoadout.IsOrk(back), session.Working.ClassAt(slot)?.Role ?? "empty");
            session.Edit(p => LoadoutEdits.SetRole(p, slot, OrkLoadout.Role, catalog).Profile);   // the Ork slot again, for the faithful checks
            await Frames(3);

            // The review's leak: the Ork DEFAULTS opened last in Extended, then a marine slot, then Faithful.
            creator.OpenSelection(Selection.DefaultFor(OrkLoadout.Role));
            await Frames(3);
            int marineSlot = session.Working.Classes.FirstOrDefault(c => !OrkLoadout.IsOrk(c.Role))?.Slot ?? 0;
            if (marineSlot > 0) { creator.OpenSelection(Selection.CustomSlot(marineSlot)); await Frames(3); }

            // Faithful: nothing of the Ork is offered; the saved Ork loadout is kept and joins as the side's default.
            _services.SetContent(ContentExtras.None);
            _front.ShowJoin();
            await Frames(4);
            _front.ShowCreator();
            await Frames(4);
            Check("faithful: the creator's strip shows no Ork DEFAULTS chip after the Ork DEFAULTS were open in Extended",
                FindButton(creator, b => b.Text.StartsWith("DEFAULTS") && b.Text.Contains("Ork", StringComparison.OrdinalIgnoreCase)) is null);
            var orkChip = FindButton(creator, b => b.Text.StartsWith($"CUSTOM {slot}\n"));
            orkChip?.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(4);
            Check("faithful: the saved Ork slot's chip does not open the Ork editor", orkChip is not null && !creator.IsOrkOpen && creator.Open != Selection.CustomSlot(slot) && !creator.Preview.ShowsOrk,
                $"chip {orkChip is not null}, open {creator.Open}");
            creator.OpenSelection(Selection.CustomSlot(slot));
            await Frames(3);
            Check("faithful: opening the saved Ork slot directly falls back to a marine loadout", !creator.IsOrkOpen);
            creator.OpenSelection(Selection.DefaultFor(OrkLoadout.Role));
            await Frames(3);
            Check("faithful: opening the Ork DEFAULTS directly falls back to a marine loadout", !creator.IsOrkOpen);
            _front.ShowJoin();
            await Frames(4);
            Check("faithful: the Join screen offers no Ork DEFAULTS", FindButton(_front.JoinScreen!, b => b.Text.Contains("Ork Boy (either side)")) is null);
            _front.ShowCreator();
            await Frames(4);
            Check("faithful: the creator has no Orks faction and no Ork Boy tab", FindButton(creator, b => b.Text == "Orks") is null && FindButton(creator, b => b.Text == "Ork Boy" && b.ToggleMode) is null);
            var faithfulPlan = _services.PlanFor(Selection.CustomSlot(slot));
            Check("faithful: the saved Ork loadout joins as the side's default class with no Ork look",
                faithfulPlan is not null && faithfulPlan.Choice.Class != orkClass && faithfulPlan.OrkLook == Races.OrkLookAutomatic && faithfulPlan.Summary.Contains("added content"), faithfulPlan?.Summary ?? "null");
            var faithfulDefaults = _services.PlanFor(Selection.DefaultFor(OrkLoadout.Role));
            Check("faithful: the Ork DEFAULTS join as the side's default class", faithfulDefaults is not null && faithfulDefaults.Choice.Class != orkClass && faithfulDefaults.Summary.Contains("added content"), faithfulDefaults?.Summary ?? "null");
            var faithfulHello = FrontEndLaunch.Apply(new SmNet.ClientOptions(), new JoinRequest("127.0.0.1:27650", faithfulPlan!, _services, ContentExtras.None));
            Check("faithful: the Hello carries no Ork class or look", faithfulHello.RequestedClass != orkClass && faithfulHello.OrkLook == Races.OrkLookAutomatic, $"{faithfulHello.RequestedClass}/{faithfulHello.OrkLook}");
            Check("faithful: the Ork loadout stays in the profile", session.Working.ClassAt(slot)?.Role == OrkLoadout.Role);
        }
        finally
        {
            _services.Content = originalContent;
            _services.UpdateSettings(x => x with { Content = originalName });
            session.Seed(kept);
            session.Save();   // the file as the run found it before these checks (they saved an Ork loadout into it)
            await Frames(3);
            _front.ShowJoin();
            await Frames(3);
        }
    }
}
