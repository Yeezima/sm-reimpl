using System;
using System.Linq;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmClient.Looks;
using SmContent.Customization;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Customizer T7: the in-match loadout picker (docs/customizer-design.md F2, acceptance M1 to M3). Esc (alive or dead) opens the class creator
/// (<see cref="CreatorView"/> with <c>Banner</c> and <c>Deploy</c> set; its appearance editor too) with the mouse free and no game input sent; Deploy builds the same
/// <see cref="JoinPlan"/> a Join does for the side the player is on, sends it with <see cref="GameClient.ChangeSetup"/> and closes; the server applies it at the next respawn
/// (<see cref="SetupAck"/> says accepted, defaulted fields or emblem trouble: the death screen shows it). SET ACTIVE sends the same way and keeps the menu open; Esc or Back
/// sends only a saved active loadout that changed since the menu opened (fix/class-change-in-match, <see cref="PickerRules.SendOnClose"/>). The picker closes by itself at the respawn;
/// closing restores the mouse capture (never in a test window, a bot or a replay). There is no C key (removed 2026-10-30, owner). The rules are in
/// <c>client/Looks/LookLogic.cs</c> (<see cref="PickerRules"/>).
/// <para>
/// Test aids (client only): <c>--bot-change-loadout &lt;slot|default:role&gt; [seconds]</c> (a bot with <c>--profile</c> changes loadout that long after it dies),
/// <c>--picker-demo [seconds]</c> (opens the picker that long after connecting, alive or not), <c>--picker-deploy &lt;slot|default:role&gt;</c> (a second after the picker opened it presses Deploy
/// for that loadout; the same code path as the button).
/// </para>
/// </summary>
public partial class NetworkView
{
    CustomizerServices? _pickerServices;
    CanvasLayer? _pickerLayer, _hintLayer;
    CreatorView? _pickerCreator;
    AppearanceView? _pickerAppearance;
    Label? _loadoutHint;
    bool _pickerWanted, _pickerForced;
    double _pickerOpenedAt, _ackShownUntil;
    double _deadFor;
    bool _botChanged, _demoOpened, _demoDeployed, _deathOpened;
    // fix/class-change-in-match: what the next life uses as far as this client knows (sent, or the saved active loadout when the menu opened), whether the menu was
    // closed by DEPLOY, the class last sent, the last ack logged, the menu's line under the banner, and the line after a respawn that changed the class.
    LoadoutKey? _pickerInEffect;
    bool _pickerDeployed;
    /// <summary>Why the last send could not save the choice in the profile (null: saved, or no save asked for).</summary>
    string? _lastSaveError;
    int _sentClass = -1;
    ushort _ackLogged;
    string _pickerNote = "";
    int _classBefore = -1;
    bool _aliveBefore;
    string _respawnLine = "";
    double _respawnLineUntil;

    /// <summary>The picker is on screen (the mouse is free and the game takes no input).</summary>
    bool PickerOpen => _pickerLayer is not null;
    /// <summary>The menu is open or being opened (the customizer is still loading): the mouse is free and the game takes no input.</summary>
    bool PickerBusy => _pickerLayer is not null || _pickerWanted;

    /// <summary>Set by the shell that started this view (<c>ModelViewer</c>): returns to the front end's Join screen (the T8 back path). Null: no "LEAVE MATCH" button.</summary>
    public Action? LeaveToMenu { get; set; }

    /// <summary>The own player counts as alive until the first snapshot has told who it is (id 0 is the state before it): no "dead" flash while joining.</summary>
    static bool UiAlive(PlayerState me) => me.Alive || me.PlayerId <= 0;

    // The test aids' flags, read once (not every frame).
    bool _aidsRead, _aidPickerOnDeath, _aidPickerDemo, _aidPickerLeave;
    double _aidPickerDemoAfter = 6.0;
    string? _aidPickerDeploy, _aidPickerActivate;
    bool _aidPickerResume, _demoActivated;
    double _demoActivatedAt;
    string[] _aidBotChange = [];

    void ReadLoadoutAids()
    {
        if (_aidsRead) return;
        _aidsRead = true;
        _aidPickerOnDeath = CmdFlag("picker-on-death");
        _aidPickerDemo = CmdFlag("picker-demo");
        _aidPickerLeave = CmdFlag("picker-leave");
        if (double.TryParse(CmdArg("picker-demo"), System.Globalization.CultureInfo.InvariantCulture, out var after)) _aidPickerDemoAfter = after;
        _aidPickerDeploy = CmdArg("picker-deploy");
        _aidPickerActivate = CmdArg("picker-activate");
        _aidPickerResume = CmdFlag("picker-resume");
        _aidBotChange = CmdArgs("bot-change-loadout", 2);
    }

    static string[] CmdArgs(string name, int count)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--" + name);
        if (i < 0) return [];
        return args.Skip(i + 1).TakeWhile(a => !a.StartsWith("--")).Take(count).ToArray();
    }

    // ---- services ----

    /// <summary>
    /// The customizer's services for the picker: the front end's (the Join's, profile and emblems included), else a fresh set on the profile <c>--profile</c> names or the
    /// player's own (a plain <c>--connect</c> by a human). Bots never open the player's own profile: they need <c>--profile</c>.
    /// </summary>
    CustomizerServices? PickerServices(bool bot)
    {
        if (_pickerServices is not null) return _pickerServices;
        if (SmClient.FrontEnd.FrontEndLaunch.Current?.Services is { } shared) return _pickerServices = OfMatch(shared);
        string? profile = CmdArg("profile");
        if (bot && profile is null) return null;
        return _pickerServices = OfMatch(new CustomizerServices(_gameDir, profile, lockDemo: false));
    }

    /// <summary>The picker offers what the match's own profile has (added content E2: no Ork in a faithful match, whatever the saved choice says).</summary>
    CustomizerServices OfMatch(CustomizerServices services)
    {
        if (_config is not null) services.Content = _config.Extras;
        return services;
    }

    // ---- input ----

    /// <summary>Called first by <c>_UnhandledInput</c> for humans: Esc opens the picker; while it is open the picker owns the mouse and the keys.</summary>
    bool LoadoutInput(InputEvent e)
    {
        if (PickerOpen) return true;
        // Esc opens the customizer menu in a match, alive or dead (M44); the front end's back path keeps Esc while the join is going nowhere (ModelViewer handles it then).
        if (IsMenuPress(e) && _client is not null)   // Esc, or Start on a controller
        {
            bool connected = _client.Status == ClientStatus.Connected;
            var action = PickerRules.OnEscape(connected, ConnectionFailed || StillConnecting, PickerOpen, human: !_bot && _demoPlayback is null);
            // Esc while "Loading your loadouts..." cancels the request: the mouse is back and the game takes input again.
            if (_pickerWanted && !PickerOpen) { FailOpen(); return true; }
            // The Esc is consumed only when the menu opens; if it cannot, the mouse is back and Esc falls through.
            return action == PickerRules.EscAction.OpenMenu && OpenPicker(forced: true);
        }
        return false;
    }

    // ---- open / close ----

    /// <summary>
    /// Asks for the menu. From this moment the mouse is free and the game takes no input (<see cref="PickerBusy"/>), also while the customizer is still loading (the hint says
    /// "Loading your loadouts..."; <c>LoadoutFrame</c> builds it when ready). False when it cannot be built (a bot without --profile, a customizer that failed): the mouse is
    /// captured again and nothing was consumed.
    /// </summary>
    bool OpenPicker(bool forced)
    {
        if (!_pickerWanted) _pickerForced = forced;   // a request already under way keeps its kind
        _pickerWanted = true;
        if (_pickerLayer is not null) return true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        var services = PickerServices(_bot);
        if (services is null) { GD.PrintErr("loadout picker: a bot needs --profile <file>"); FailOpen(); return false; }
        if (services.Failed) { _notes.Add("loadout picker: " + services.Error); GD.PrintErr("loadout picker: " + services.Error); FailOpen(); return false; }
        if (!services.Ready) return true;
        BuildPicker(services);
        return true;
    }

    (bool Alive, int Seconds) _bannerShown = (false, -1);

    /// <summary>The banner changes only with the alive flag or the whole seconds of the respawn countdown: the text is built then, not every frame.</summary>
    void SetBanner(PlayerState me)
    {
        var key = (me.Alive, Math.Max(0, (int)MathF.Ceiling(me.RespawnLeft)));
        if (_pickerCreator is null || key == _bannerShown) return;
        _bannerShown = key;
        _pickerCreator.Banner = PickerRules.Banner(me.Alive, me.RespawnLeft) + (_pickerNote.Length > 0 ? "\n" + _pickerNote : "");
    }

    void FailOpen() { _pickerWanted = false; RestoreMouse(leaving: false); }

    /// <summary>Captures the mouse again where a human is back in a connected match (never in a test window, for a bot or a replay, after a failed connection or when leaving).</summary>
    void RestoreMouse(bool leaving)
    {
        if (PickerRules.RecaptureMouse(!_bot && _demoPlayback is null && _error == "", ModelViewer.TestWindow, _client?.Status == ClientStatus.Connected, leaving))
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    bool? _overlayTranslucent;

    /// <summary>The match stays visible behind the menu while the player is alive (a dim, an opaque panel); the death screen and the match's end are opaque.</summary>
    void ApplyPickerLook(bool translucent)
    {
        if (_overlayTranslucent == translucent) return;
        _overlayTranslucent = translucent;
        if (_pickerCreator is not null) MatchOverlay.Apply(_pickerCreator, translucent);
        if (_pickerAppearance is not null) MatchOverlay.Apply(_pickerAppearance, translucent);
    }

    /// <summary>LEAVE MATCH confirmed (or the aid): the menu goes, the preview assets are released (the old services stay unpinned) and the front end takes over.</summary>
    void LeaveMatchNow()
    {
        ClosePicker(leaving: true);
        _pickerServices?.Previews.Clear();
        LeaveToMenu?.Invoke();
    }

    void BuildPicker(CustomizerServices services)
    {
        _pickerWanted = false;
        services.InMatch = true;   // the preview draws no eye glow here (a second effects library would load beside the match's)
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiKit.EnterVirtualSpace(GetWindow());
        _pickerLayer = new CanvasLayer { Name = "LoadoutPicker", Layer = 60 };
        var root = new Control { Name = "PickerRoot", Theme = UiKit.Theme };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _pickerCreator = new CreatorView(services, () => ClosePicker(), ShowPickerAppearance) { Name = "Creator" };
        _pickerAppearance = new AppearanceView(services, ShowPickerCreator) { Name = "Appearance", Visible = false };
        root.AddChild(_pickerCreator);
        root.AddChild(_pickerAppearance);
        _pickerLayer.AddChild(root);
        AddChild(_pickerLayer);
        UiSounds.Current?.Attach(_pickerLayer);   // menu sounds on the picker's buttons (fidelity gap 9); the handler ends with the layer
        var me = _client!.Predicted;
        _bannerShown = (false, -1);
        _pickerNote = "";
        SetBanner(me);
        _pickerCreator.Deploy = DeployFromPicker;
        _pickerCreator.Activate = ActivateFromPicker;
        _pickerDeployed = false;
        _pickerInEffect = ActiveKey(services);
        if (_pickerForced) _pickerCreator.BackText = $"RESUME ({PadHost.Hint("Esc", "B")})";
        if (LeaveToMenu is not null) _pickerCreator.Leave = LeaveMatchNow;
        _overlayTranslucent = null;
        ApplyPickerLook(PickerRules.Translucent(UiAlive(me), _client.Match.State == MatchState.Ended));
        _pickerCreator.SetPreviewRace(me.Team == Team.Chaos ? SmContent.CustomizationCatalog.Chaos : SmContent.CustomizationCatalog.SpaceMarine);
        _pickerCreator.FocusList();
        _pickerOpenedAt = _connectedFor;
        GD.Print($"t {_connectedFor:F1} s: loadout picker opened ({(me.Alive ? "alive, forced" : "dead")}); mouse free");
    }

    void ClosePicker(bool leaving = false)
    {
        if (_pickerLayer is null) return;
        // The saved active loadout changed or was edited and saved without DEPLOY / SET ACTIVE: it is what the player respawns with (fix/class-change-in-match).
        if (_pickerServices is { Session: { } closing } services && PickerRules.SendOnClose(_pickerDeployed, leaving, _client?.Status == ClientStatus.Connected, _pickerInEffect, ActiveKey(services))
            && services.Catalog is { } catalog)
        {
            var active = LoadoutEdits.Active(closing.Saved, catalog, out _);
            if (SendLoadout(services, active, save: false, out string sent, closing.Saved)) _notes.Add("loadout: your saved active loadout goes with you at the next respawn: " + sent);
            else GD.Print("loadout picker: the active loadout was not sent: " + sent);
        }
        _pickerLayer.QueueFree();
        if (_pickerServices is not null) _pickerServices.InMatch = false;
        _pickerLayer = null; _pickerCreator = null; _pickerAppearance = null; _pickerForced = false; _pickerWanted = false;
        UiKit.LeaveVirtualSpace(GetWindow());
        RestoreMouse(leaving);
        GD.Print($"t {_connectedFor:F1} s: loadout picker closed; mouse {(Input.MouseMode == Input.MouseModeEnum.Captured ? "captured again" : "left free")}");
    }

    void ShowPickerAppearance()
    {
        if (_pickerCreator is null || _pickerAppearance is null || _pickerServices is null) return;
        var open = _pickerCreator.Open;
        if (open.Kind != SmContent.Customization.SelectionKind.Custom) return;
        string race = _client!.Predicted.Team == Team.Chaos ? SmContent.CustomizationCatalog.Chaos : SmContent.CustomizationCatalog.SpaceMarine;
        _pickerCreator.Visible = false;
        _pickerAppearance.Visible = true;
        _pickerAppearance.Open(open.Slot, race);
    }

    void ShowPickerCreator()
    {
        if (_pickerCreator is null || _pickerAppearance is null) return;
        _pickerAppearance.Visible = false;
        _pickerCreator.Visible = true;
        _pickerCreator.Reenter();
        _pickerCreator.FocusList();
    }

    // ---- deploying ----

    void DeployFromPicker(SmContent.Customization.Selection selection)
    {
        var services = _pickerServices;
        if (services is null) return;
        if (SendLoadout(services, selection, save: true, out string message)) { _pickerDeployed = true; ClosePicker(); }
        else { _pickerCreator?.Say(message, 8.0); GD.Print("loadout picker: " + message); }
    }

    /// <summary>
    /// SET ACTIVE in a match (fix/class-change-in-match): the loadout becomes the profile's active one (saved) and is sent for the next respawn, as DEPLOY does, but the
    /// menu stays open. Returns the line the creator shows: the class the player respawns as on its side (and that a class name of the other side plays as its counterpart).
    /// </summary>
    string ActivateFromPicker(SmContent.Customization.Selection selection)
    {
        var services = _pickerServices;
        if (services is null) return "The customizer is not ready.";
        if (!SendLoadout(services, selection, save: true, out string message)) { GD.Print("loadout picker: " + message); return message; }
        string line = message;
        if (services.Catalog is { } catalog && services.Session is { } session && _client is not null)
        {
            string role = selection.Kind == SmContent.Customization.SelectionKind.Custom ? session.Working.ClassAt(selection.Slot)?.Role ?? "" : selection.Role ?? ProfileStore.FirstRole(catalog);
            var team = _client.Predicted.Team;
            string race = team == Team.Chaos ? SmContent.CustomizationCatalog.Chaos : SmContent.CustomizationCatalog.SpaceMarine;
            string shownRace = _pickerCreator?.PreviewRace ?? race;
            line = PickerRules.ActivatedText(LoadoutRows.ClassName(catalog, role, race), SideName(team), LoadoutRows.ClassName(catalog, role, shownRace),
                shownRace == SmContent.CustomizationCatalog.Chaos ? "Chaos" : "Space Marines");
        }
        if (_lastSaveError is { } saveError) line += " " + PickerRules.NotSavedText(saveError);
        _pickerNote = line;
        _bannerShown = (false, -1);   // the banner shows the line under its own
        GD.Print($"t {_connectedFor:F1} s: loadout picker: SET ACTIVE: {line}");
        return line;
    }

    /// <summary>What the saved active loadout would send now (side of the own player), or null before the customizer and the game data are ready.</summary>
    LoadoutKey? ActiveKey(CustomizerServices services)
    {
        if (_config is null || _client is null || services.Session is not { } session || services.Catalog is not { } catalog) return null;
        var saved = session.Saved;
        var plan = JoinPlan.Build(saved, LoadoutEdits.Active(saved, catalog, out _), _config, catalog, _client.Predicted.Team == Team.Chaos ? SideChoice.Chaos : SideChoice.SpaceMarines);
        return new LoadoutKey(plan.Choice, plan.SpaceMarineLook, plan.ChaosLook, plan.OrkLook);
    }

    /// <summary>A class's display name for the player ("Devastator"), from the catalogue when it is loaded, else the class's data name.</summary>
    string ClassDisplayName(int playerClass)
    {
        if (_config is null || playerClass < 0 || playerClass >= _config.Classes.Count) return ClassLabel(playerClass);
        var c = _config.Classes[playerClass];
        if (_pickerServices?.Catalog is { } catalog && c.Role.Length > 0 && ProfileWire.RaceOf(c.Side) is { } race) return LoadoutRows.ClassName(catalog, c.Role, race);
        return ClassLabel(playerClass);
    }

    /// <summary>
    /// Sends <paramref name="selection"/> as the loadout and looks for the next life: the plan for the side the player is on (class by role, items by name), then the same
    /// <c>Setup</c> a Join's Hello would carry. The own model changes at the respawn (<see cref="OwnLooks"/>); the new look is built meanwhile (<see cref="PrebuildLooks"/>).
    /// <paramref name="from"/>: the profile to build from (default the session's working copy; the saved one when the menu closes).
    /// </summary>
    bool SendLoadout(CustomizerServices services, SmContent.Customization.Selection selection, bool save, out string message, CustomizationProfile? from = null)
    {
        message = "";
        if (_client is null || _client.Status != ClientStatus.Connected) { message = "Not connected to the server."; return false; }
        if (_config is null || services.Session is not { } session || services.Catalog is not { } catalog) { message = "The game data is not ready yet."; return false; }
        var profile = from ?? session.Working;
        if (!LoadoutEdits.IsValid(profile, selection)) { message = selection.Kind == SmContent.Customization.SelectionKind.Custom ? $"Slot {selection.Slot} is empty. Fill it first." : "That loadout is not available."; return false; }
        var team = _client.Predicted.Team;
        var plan = JoinPlan.Build(profile, selection, _config, catalog, team == Team.Chaos ? SideChoice.Chaos : SideChoice.SpaceMarines);
        _lastSetupSequence = _client.ChangeSetup(plan.Choice, plan.SpaceMarineLook, plan.ChaosLook, plan.OrkLook);   // ui/customizer-ork: the profile's Ork look rides along
        _ownOrkLook = plan.OrkLook;
        _setupSent = true; _setupSentAt = _connectedFor; _ackShownUntil = double.MaxValue;
        _sentClass = plan.Choice.Class; _ackLogged = 0;
        _ownLooks.Request(plan.SpaceMarineLook, plan.ChaosLook);
        PrebuildLooks(plan.Choice, plan.SpaceMarineLook, plan.ChaosLook);
        string? saveError = null;
        if (save)
        {
            session.Edit(p => LoadoutEdits.Select(p, selection));
            saveError = session.Save();
            if (saveError is not null) _notes.Add("loadout picker: could not remember the choice: " + saveError);
        }
        _lastSaveError = saveError;
        // A failed save leaves the old active loadout in the saved profile: that counts as in effect, so closing the menu does not send it back (fix/set-active-save-failure).
        _pickerInEffect = PickerRules.InEffectAfterSend(new LoadoutKey(plan.Choice, plan.SpaceMarineLook, plan.ChaosLook, plan.OrkLook), saveError is null ? null : ActiveKey(services), saveError);
        message = plan.Summary;
        GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} sends a loadout change (setup {_lastSetupSequence}): {plan.Summary}");
        return true;
    }

    // ---- per frame ----

    void LoadoutFrame(PlayerState me, double delta)
    {
        if (_client is null) return;
        EnsureLoadoutHint();
        RunLoadoutAids(me, delta);
        bool ended = _client.Match.State == MatchState.Ended;
        // A C pressed while the customizer was still loading must not open the picker later, after the respawn.
        if (_pickerWanted && _pickerLayer is null && !_pickerForced && !PickerRules.CanOpen(_client.Status == ClientStatus.Connected, UiAlive(me), Spectate is not null, ended)) FailOpen();
        if (_pickerWanted && _pickerLayer is null) OpenPicker(_pickerForced);
        if (_pickerLayer is not null)
        {
            // The respawn closes the picker (the change applies from that life on); so does anything that makes C unavailable.
            if (!_pickerForced && !PickerRules.CanOpen(_client.Status == ClientStatus.Connected, UiAlive(me), Spectate is not null, ended)) ClosePicker();
            else if (_pickerCreator is not null) SetBanner(me);
            ApplyPickerLook(PickerRules.Translucent(UiAlive(me), ended));
        }
        UpdateLoadoutHint(me, ended);
    }

    void EnsureLoadoutHint()
    {
        if (_loadoutHint is not null) return;
        _hintLayer = new CanvasLayer { Name = "LoadoutHint", Layer = 3 };
        _loadoutHint = new Label { Name = "Hint", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _loadoutHint.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        _loadoutHint.OffsetTop = 428; _loadoutHint.OffsetBottom = 520;
        _loadoutHint.AddThemeFontSizeOverride("font_size", 24);
        _loadoutHint.AddThemeColorOverride("font_color", new Color(1f, 0.8f, 0f));
        _loadoutHint.AddThemeColorOverride("font_outline_color", Colors.Black);
        _loadoutHint.AddThemeConstantOverride("outline_size", 5);
        _hintLayer.AddChild(_loadoutHint);
        AddChild(_hintLayer);
    }

    /// <summary>The death screen's lines: the key hint while down, "Loading" while the customizer reads the install, the answer to the last change.</summary>
    string _hintFirst = "", _hintSecond = "";

    void UpdateLoadoutHint(PlayerState me, bool ended)
    {
        if (_loadoutHint is null || _client is null) return;
        string first = "", second = "";
        bool down = PickerRules.CanOpen(true, UiAlive(me), Spectate is not null, ended);
        if (_pickerWanted && _pickerLayer is null) first = "Loading your loadouts...";
        else if (down && !PickerOpen && !_bot) first = $"[{PadHost.Hint("ESC", "START")}] CHANGE LOADOUT";
        var ack = _client.LastSetupAck is { } a && a.Sequence == _lastSetupSequence ? a : null;
        string? accepted = ack is not null && ack.Loadout.Class >= 0 ? ClassDisplayName(ack.Loadout.Class) : null;
        string? sentName = _sentClass >= 0 ? ClassDisplayName(_sentClass) : null;
        var (text, problem) = PickerRules.AckText(_client.SetupPending, _connectedFor - _setupSentAt, ack, _setupSent, accepted, sentName, SideName(me.Team));
        if (ack is not null && _ackLogged != ack.Sequence)
        {
            _ackLogged = ack.Sequence;
            GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} setup {ack.Sequence} acknowledged: next life class {ack.Loadout.Class} '{ClassLabel(ack.Loadout.Class)}' (flags {ack.Flags}): {text}");
        }
        // The respawn that changed the class says so for a few seconds (the ack's "applies when you respawn" has come true).
        if (me.Alive && !_aliveBefore && me.PlayerId > 0)
        {
            string line = PickerRules.RespawnText(_classBefore, me.Class, ClassDisplayName(me.Class));
            if (line.Length > 0)
            {
                _respawnLine = line; _respawnLineUntil = _connectedFor + 4.0; _ackShownUntil = 0;
                GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} respawned as class {me.Class} '{ClassLabel(me.Class)}' (was {_classBefore} '{ClassLabel(_classBefore)}')");
            }
        }
        _aliveBefore = me.Alive;
        if (me.PlayerId > 0) _classBefore = me.Class;
        if (text.Length > 0 && (down || _connectedFor < _ackShownUntil && _connectedFor - _setupSentAt < 8.0)) second = text;
        else if (problem && text.Length > 0 && _connectedFor - _setupSentAt < 8.0) second = text;   // a refused or remapped change is said even after the respawn
        if (_connectedFor < _respawnLineUntil) second = _respawnLine;
        if (first != _hintFirst || second != _hintSecond)
        {
            _hintFirst = first; _hintSecond = second;
            _loadoutHint.Text = first.Length > 0 && second.Length > 0 ? first + "\n" + second : first.Length > 0 ? first : second;
        }
        _loadoutHint.Visible = (first.Length > 0 || second.Length > 0) && !NoHud && !PickerOpen && !NoMarkers && Spectate is null;
    }

    // ---- test aids ----

    void RunLoadoutAids(PlayerState me, double delta)
    {
        if (_client is null || _config is null) return;
        ReadLoadoutAids();
        _deadFor = UiAlive(me) ? 0 : _deadFor + delta;        // --bot-change-loadout <slot|default:role> [seconds]: after dying, the bot changes its loadout (and look) through the same path as the picker's Deploy.
        if (_bot && !_botChanged && _aidBotChange is { Length: > 0 } botArgs)
        {
            double wait = botArgs.Length > 1 && double.TryParse(botArgs[1], System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1.0;
            if (!UiAlive(me) && _deadFor >= wait)
            {
                _botChanged = true;
                if (PickerServices(bot: true) is { } services)
                {
                    services.LoadTask.Wait();
                    if (services.Ready && SmClient.Customizer.Model.LaunchOptions.ParseLoadout(botArgs[0], services.Catalog!) is { } selection)
                    {
                        if (!SendLoadout(services, selection, save: false, out string why)) GD.PrintErr($"t {_connectedFor:F1} s: bot loadout change refused: {why}");
                    }
                    else GD.PrintErr($"--bot-change-loadout '{botArgs[0]}': {(services.Ready ? "neither default:<role> nor a slot 1 to 10" : "the customizer could not start: " + services.Error)}");
                }
                else GD.PrintErr("--bot-change-loadout needs --profile <file>");
            }
        }
        // --picker-on-death (bot or test window): opens the picker (not forced) as soon as the own player is dead, as C would; the respawn closes it by itself.
        if (UiAlive(me)) _deathOpened = false;
        else if (!_deathOpened && !PickerOpen && !_pickerWanted && _aidPickerOnDeath) { _deathOpened = true; OpenPicker(forced: false); }
        // --picker-demo [seconds]: open the picker once, alive or not (screenshots in a test window); --picker-deploy <loadout>: press Deploy a second later.
        if (!_bot && !_demoOpened && _aidPickerDemo)
        {
            double after = _aidPickerDemoAfter;
            if (_connectedFor >= after) { _demoOpened = true; OpenPicker(forced: true); }
        }
        // --picker-leave: a second and a half after the picker opened, as if LEAVE MATCH had been confirmed (back to the front end).
        if (_pickerCreator is not null && _pickerForced && _aidPickerLeave && LeaveToMenu is not null && _connectedFor - _pickerOpenedAt >= 1.5) { LeaveMatchNow(); return; }
        // --picker-activate <loadout> (fix/class-change-in-match): a second after the picker opened (Esc, --picker-demo or --picker-on-death), opens that loadout and presses
        // SET ACTIVE, the owner's way; with --picker-resume the menu is left a second later through RESUME / BACK (the creator's Back, as Esc would).
        if (!_demoActivated && _pickerCreator is not null && _aidPickerActivate is { } activate && _connectedFor - _pickerOpenedAt >= 1.0)
        {
            _demoActivated = true; _demoActivatedAt = _connectedFor;
            if (SmClient.Customizer.Model.LaunchOptions.ParseLoadout(activate, _pickerServices!.Catalog!) is { } selection) { _pickerCreator.OpenSelection(selection); _pickerCreator.PressSetActive(); }
            else GD.PrintErr($"--picker-activate '{activate}' is neither default:<role> nor a slot 1 to 10");
        }
        if (_demoActivated && _aidPickerResume && _pickerCreator is not null && _connectedFor - _demoActivatedAt >= 1.0) { _aidPickerResume = false; _pickerCreator.Back(); }
        if (!_demoDeployed && _pickerCreator is not null && _pickerForced && _aidPickerDeploy is { } what && _connectedFor - _pickerOpenedAt >= 1.5)
        {
            _demoDeployed = true;
            if (SmClient.Customizer.Model.LaunchOptions.ParseLoadout(what, _pickerServices!.Catalog!) is { } selection) DeployFromPicker(selection);
            else GD.PrintErr($"--picker-deploy '{what}' is neither default:<role> nor a slot 1 to 10");
        }
    }
}
