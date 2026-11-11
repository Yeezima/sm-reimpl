using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient.Customizer;

/// <summary>
/// What the front end and the customizer share for one run: the install, the catalogue, the profile session, the simulation's lists (the indices a Join sends are
/// derived from them), the remembered settings and the preview's assets. The slow reads (paint schemes, catalogue, gameplay tuning) run on a worker thread;
/// the screens wait for <see cref="Ready"/>. The profile lives in Godot's user directory (<c>%APPDATA%\sm-reimpl</c>; project setting) unless <c>--profile</c>
/// names a file: then the profile, its emblems and the front-end settings stay next to that file. Never the repository, never the game install.
/// </summary>
public sealed partial class CustomizerServices
{
    public string GameDir { get; }
    public string SettingsDirectory { get; }
    public ProfileStore Store { get; }
    public bool UsesProfileFile { get; }
    public UnlockPolicy Policy { get; }
    public List<string> Notes { get; } = new();
    public FrontEndSettings Settings { get; private set; }

    /// <summary>
    /// The content profile this run plays (added content E2, docs/added-content.md): <c>--content</c> when given, else the saved choice of the front end (default
    /// <see cref="ContentProfiles.DefaultName"/>, extended: the owner's decision of 2026-10-29). The lobby menu's CONTENT row changes it (<see cref="SetContent"/>); the
    /// in-match picker sets it to the match's own flags. The customizer offers only the factions it allows (<see cref="Factions"/>), and a Join never asks for more.
    /// </summary>
    public ContentExtras Content
    {
        get => _content;
        set { _content = value; ApplyProfileView(); }
    }
    ContentExtras _content;
    readonly object _viewLock = new();

    /// <summary>
    /// Puts the profile view of the content profile into the session (musou/loadout-slots, 2026-11-10): an added mode's own loadout slots (<see cref="ExtrasProfileView"/>), or none (the profile's own
    /// slots, Classic PvP). Called when the content profile changes, when the profile is loaded, and by a screen that changes what its view shows.
    /// </summary>
    public void ApplyProfileView() => ApplyProfileView(Session);

    void ApplyProfileView(ProfileSession? session)
    {
        if (session is null) return;
        lock (_viewLock)
        {
            IProfileView? view = null;
            ExtrasProfileView(ref view);   // EXTRAS-HOOK: Musou Mode's own loadout slots
            session.UseView(view);
        }
    }

    /// <summary>
    /// Hook for added content (docs/added-content.md; client/Extras/Musou/Customizer/CustomizerServices.Musou.cs): the view of the profile the screens edit for this content profile. Leaves it null (the
    /// profile's own slots) unless its content profile is on.
    /// </summary>
    partial void ExtrasProfileView(ref IProfileView? view);

    CustomizationCatalog? _catalog;
    (ContentExtras Content, CustomizationCatalog Data, CustomizationCatalog Shown)? _contentCatalog;

    /// <summary>
    /// The catalogue as the content profile in effect (<see cref="Content"/>) offers it: <see cref="CustomizationCatalog.For"/>, so Musou's profile lists its arsenal's items (W0, docs/new-weapons-design.md
    /// section 8, hook 3) and every other profile the data's catalogue itself. The profile is read and repaired with the data's (<see cref="ProfileSession.Catalog"/>). Kept per content profile.
    /// </summary>
    public CustomizationCatalog? Catalog
    {
        get
        {
            if (_catalog is not { } data) return null;
            var content = Content;
            if (_contentCatalog is { } known && known.Content == content && ReferenceEquals(known.Data, data)) return known.Shown;
            var shown = data.For(content);
            _contentCatalog = (content, data, shown);
            return shown;
        }
        private set => _catalog = value;
    }
    public ProfileSession? Session { get; private set; }
    SimConfig? _config;
    /// <summary>
    /// The configuration the customizer plans a Join with: the data's, or (added content, <see cref="ExtrasConfig"/>) the one of the content profile in effect, so a name its arsenal adds (Musou's autocannon)
    /// resolves to an index. Null while that configuration is still being read, so <see cref="Ready"/> waits for it.
    /// </summary>
    public SimConfig? Config
    {
        get { var config = _config; ExtrasConfig(ref config); return config; }   // EXTRAS-HOOK: Musou's own config for a Musou profile (client/Extras/Musou/Arsenal/CustomizerServices.Arsenal.cs)
        private set => _config = value;
    }

    /// <summary>Hook for added content: replaces <paramref name="config"/> (the data's) by the one its content profile plays with; null while that is read. Leaves it alone unless its content profile is on.</summary>
    partial void ExtrasConfig(ref SimConfig? config);

    /// <summary>Hook for added content: runs at the end of the load, on its worker, so what the content profile needs beyond the data's configuration is read before <see cref="LoadTask"/> completes (a caller that waits on it then finds <see cref="Ready"/> true).</summary>
    partial void ExtrasLoaded();
    /// <summary>Whether each perk does anything in the simulation (the creator's NOT ACTIVE YET / PARTLY ACTIVE words); read once with the config.</summary>
    public IReadOnlyDictionary<string, PerkStatus>? PerkActivities { get; private set; }
    public string? Error { get; private set; }
    public bool Ready => Catalog is not null && Session is not null && Config is not null;
    public bool Failed => Error is not null;
    readonly string? _refused;
    public Task LoadTask { get; }

    /// <summary>The services are used by the in-match picker (<c>C</c>): the 3D preview then draws no eye glow, since the match has its own effects and a second library would load beside it.</summary>
    public bool InMatch { get; set; }

    /// <summary>These services were made for a match's picker (<c>NetworkView.OfMatch</c>) and stay so: unlike <see cref="InMatch"/> it is never reset, so what only the front end needs (Musou's own configuration for a Join, <see cref="ExtrasConfig"/>) is never read for a match.</summary>
    public bool MadeForMatch { get; set; }

    public PreviewAssets Previews { get; }

    public CustomizerServices(string gameDir, string? profileFile, bool lockDemo, string? contentArg = null)
    {
        GameDir = gameDir;
        Policy = lockDemo ? UnlockPolicy.Demo : UnlockPolicy.All;
        UsesProfileFile = !string.IsNullOrWhiteSpace(profileFile);
        string userDir = ProjectSettings.GlobalizePath("user://");
        if (UsesProfileFile)
        {
            // --profile never points into the repository or the game install (profiles and emblems are the player's, never the project's; the install is read-only).
            string repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
            string full = System.IO.Path.GetFullPath(profileFile!);
            if (SmFormats.InstallPaths.IsWithin(full, repoRoot) || SmFormats.InstallPaths.IsWithin(full, gameDir))
            {
                _refused = "--profile must not be inside the repository or the game install: " + System.IO.Path.GetFileName(full);
                profileFile = null;
                UsesProfileFile = false;
            }
        }
        Store = UsesProfileFile ? ProfileStore.ForFile(profileFile!) : new ProfileStore(userDir);
        SettingsDirectory = UsesProfileFile ? Store.Directory : userDir;
        Settings = FrontEndSettings.Load(SettingsDirectory);
        ControlsInput.UseDirectory(SettingsDirectory);   // ui/keybinds: controls.json beside the settings
        Content = Settings.ContentExtras;
        if (contentArg is not null)
        {
            if (ContentProfiles.TryParse(contentArg, out var asked, out string contentError)) Content = asked;
            else GD.PrintErr(contentError + "; the saved profile is used");
        }
        UiKit.ReduceMotion = Settings.ReduceMotion;
        FovMath.Selected = Settings.FovDegrees;
        MouseSensitivity.Selected = Settings.MouseSensitivity;
        LiveOptions.Current = Settings.Options;
        NoteWindowChoice();
        Latest = this;
        SmClient.FrontEnd.OptionsHost.Apply(Settings.Options);   // ui/menu-rebuild: the saved options, from the first frame
        Previews = new PreviewAssets(this);
        LoadTask = Task.Run(Load);
    }

    void Load()
    {
        try
        {
            if (_refused is not null) throw new InvalidOperationException(_refused);
            var notes = new List<string>();
            var paint = MarineAssets.Schemes(GameDir, notes);
            var catalog = CustomizationCatalog.Load(GameDir, notes, paint);
            // ui/customizer-ork: a build with added content reads the Ork Boy too (appended as the last class, so the six classes' indices and values are the
            // same), so the creator and a Join can name his class; the content profile decides whether he is offered (ContentGate, JoinPlan.Build).
            var tuning = ContentExtras.Compiled ? GameplayTuning.LoadForMatch(GameDir, ContentExtras.Orks) : GameplayTuning.Load(GameDir);
            // The profile is read and repaired with the data's catalogue plus the items a Classic profile with added content offers (Classic PvP's autocannon, 2026-11-11: catalog.For adds them only
            // in a build with added content), so a slot saved under Extended keeps its autocannon whichever profile the front end opens with; what a page shows is still Catalog (its own profile's).
            var session = ProfileSession.Open(catalog.For(ContentExtras.Autocannon), Store);
            lock (Notes) { Notes.AddRange(notes); Notes.AddRange(tuning.Notes); }
            PerkActivities = PerkStatuses.Of(tuning.Config);
            Config = tuning.Config;
            lock (_viewLock)
            {
                Session = session;
                ApplyProfileView(session);   // before any screen listens: the content profile's view from the start
            }
            Catalog = catalog;
            ExtrasLoaded();   // EXTRAS-HOOK: the added content's own reads finish before LoadTask does (Musou's own configuration)
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Error = ex.Message;
        }
    }

    public void UpdateSettings(Func<FrontEndSettings, FrontEndSettings> change)
    {
        Settings = change(Settings);
        UiKit.ReduceMotion = Settings.ReduceMotion;
        FovMath.Selected = Settings.FovDegrees;
        MouseSensitivity.Selected = Settings.MouseSensitivity;
        LiveOptions.Current = Settings.Options;
        NoteWindowChoice();
        SmClient.FrontEnd.OptionsHost.Apply(Settings.Options);
        Settings.Save(SettingsDirectory);
    }

    /// <summary>The services made last (the front end's, or the match's own): F11 changes the window mode in these, so it is saved like the Options screen's row.</summary>
    public static CustomizerServices? Latest { get; private set; }

    /// <summary>The fullscreen choice (fullscreen or borderless) the player used last; F11 from a window goes back to it. Fullscreen until one is chosen.</summary>
    static WindowChoice _lastFullChoice = WindowChoice.Fullscreen;

    /// <summary>Raised after F11 changed the window mode, with the new options: an open Options screen shows the new value at once.</summary>
    public static event Action<GameOptions>? WindowModeToggled;

    void NoteWindowChoice()
    {
        if (Settings.Options.Window != WindowChoice.Windowed) _lastFullChoice = Settings.Options.Window;
    }

    /// <summary>F11 (ours): fullscreen on, or off again, through the same setting and the same save as the Options screen's WINDOW MODE row.</summary>
    public void ToggleFullscreen()
    {
        UpdateSettings(x => x with { Options = x.Options.WithFullscreenToggled(_lastFullChoice) });
        WindowModeToggled?.Invoke(Settings.Options);
    }

    /// <summary>The lobby menu's CONTENT row: plays <paramref name="extras"/> from now on and remembers it (outside the repository, next to the profile).</summary>
    public void SetContent(ContentExtras extras)
    {
        Content = extras;
        UpdateSettings(x => x with { Content = ContentProfiles.Name(extras) });
    }

    /// <summary>
    /// The factions the faction step offers in this profile: the races <see cref="ContentGate.Factions"/> allows (the Space Marines and Chaos, which the catalogue has;
    /// the Orks only with the Orks flag, ui/customizer-ork, when this run has the Ork Boy's class), as (race name, label).
    /// </summary>
    public IReadOnlyList<(string Race, string Label)> Factions()
    {
        var list = new List<(string Race, string Label)>();
        foreach (var race in ContentGate.Factions(Content))
        {
            if (ProfileWire.RaceName(race) is not { } name) continue;
            // The Ork has no catalogue race (no armour to edit): he is offered when the configuration has his class (a build with added content).
            if (race == Race.Orks ? !HasOrkClass : Catalog?.Race(name) is null) continue;
            list.Add((name, race switch { Race.Chaos => "Chaos", Race.Orks => "Orks", _ => "Space Marines" }));
        }
        ExtrasFactions(ref list);   // EXTRAS-HOOK: an added mode that plays one side only (Musou Mode: the Space Marines)
        return list;
    }

    /// <summary>
    /// Hook for added content (docs/added-content.md; client/Extras/Musou/Customizer/CustomizerServices.Musou.cs): the factions the customizer's screens offer, after the profile's own. Returns at once
    /// unless its content profile is on.
    /// </summary>
    partial void ExtrasFactions(ref List<(string Race, string Label)> factions);

    /// <summary>The configuration this run read has the Ork Boy's class (a build with added content whose Ork read worked).</summary>
    public bool HasOrkClass => Config?.Classes.Any(c => c.Race == Race.Orks) == true;

    /// <summary>The Orks are offered now: the profile has them (<see cref="ContentGate.OffersOrks"/>) and the configuration has the Ork Boy.</summary>
    public bool OffersOrks => ContentGate.OffersOrks(Content) && HasOrkClass;

    /// <summary>The class roles the screens offer: the catalogue's three, then the Ork Boy's when <see cref="OffersOrks"/>.</summary>
    public IReadOnlyList<string> Roles() => Catalog is not { } catalog ? [] : OffersOrks ? OrkLoadout.Roles(catalog, Content) : ProfileStore.Roles(catalog);

    /// <summary>The player's own emblem bytes by hash (the join sends them to the server on request).</summary>
    public byte[]? EmblemBytes(EmblemHash hash) => Store.LoadEmblem(hash);

    /// <summary>The plan for the profile's active loadout (or <paramref name="selection"/>) on the side the lobby menu asks for.</summary>
    public JoinPlan? PlanFor(Selection? selection = null)
    {
        if (!Ready) return null;
        var profile = Session!.Working;
        var chosen = selection ?? LoadoutEdits.Active(profile, Catalog!, out _);
        return JoinPlan.Build(profile, chosen, Config!, Catalog!, Settings.Side, Content);
    }
}
