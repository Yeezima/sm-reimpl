using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmContent;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// Fire support on the client (Musou pass 2, M9; the owner's items 10, 12 and 13): tag 11's records become the shells' whistles, bursts and shock, the Thunderhawk with its guns and tracers (paused:
/// <see cref="TfwFireRules.RunsEnabled"/>; nothing of it loads or plays), and the muffled war round the battlefield. Strikes are called with B (<c>TfwOrders.cs</c>). Only under Musou Mode's frame (<see cref="TfwFrame"/>), never on a lean bot; nothing here touches another match.
/// <para>
/// What loads, on first use, from the player's install: the effects (<see cref="FxNames"/>), the crater decal, the aircraft (<c>thunderhawk.dcm</c>, in a campaign chunk: a singleplayer-content
/// dependency, docs/discovery.md, S0) and the guns; the banks <c>vehicles</c> and, for a run's flyby, the campaign's <c>rgn_s27_r500</c>. A run's flyby or engine event that cannot sound falls back
/// (<see cref="FlybyEvents"/>).
/// </para>
/// <para>
/// Test aids: <c>--tfw-fire-test [s]</c> on a server with <c>SM_TFW_FIRE_TEST</c>: in a battle, s seconds after the landing and then every 30 s, a barrage across the view; F9 a barrage; <c>--tfw-fire-at x,y,z</c> sets the aim. <c>--tfw-fire-cam [x,y,z]</c>: a fixed camera on it (windowed captures), <c>--tfw-fire-cam-run</c> turning it to the aircraft. <c>--tfw-reduce-motion</c>: the Reduce motion setting for this run. <c>--log-sound tfw</c>: a line for each thing played.
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary>The effects this view plays (<c>master_fx</c>).</summary>
    static readonly string[] FxNames = ["artillery_explosion", "oob_explosions_artillery", "oob_explosions_artillery_less", "oob_explosion_huge", "amb_anti_aircraft_fire_valkyries", "thunderhawk_jet", "heavy_bolter_thunderhawk_tracer"];
    /// <summary>A Thunderhawk run's flyby (paused, played only by <see cref="TfwRunBegins"/>), best first: the aircraft's own (needs the campaign's bank), then the Valkyrie's.</summary>
    static readonly string[] FlybyEvents = ["play_thunderhawk_pass", "play_ig_valkyrie_flyby", "play_valkyrie_s13_flyby"];
    /// <summary>Shells: near, far.</summary>
    static readonly string[] BurstEvents = ["play_sm_missile_explosion", "play_powersource_explode_distant", "play_ork_rokk_distant_explosion"], FarBurstEvents = ["play_ork_rokk_distant_explosion", "play_impact_distant"];
    /// <summary>The war beyond the walls: (event, weight, gain dB). Named banks have no artillery or war bed (S0), so it is built of far explosions and guns; no flybys (the owner took them out, 2026-11-01).</summary>
    static readonly (string Event, float Weight, float Gain)[] WarEvents =
    [
        ("play_ork_rokk_distant_explosion", 5f, 0f), ("play_impact_distant", 4f, 0f), ("play_powersource_explode_distant", 2f, 0f),
        ("play_titan_footstep", 1f, -2f), ("play_sm_heavy_bolter_fire_single", 2f, -8f), ("play_sm_missile_explosion", 2f, -6f),
    ];

    /// <summary>The bank of the aircraft's flyby.</summary>
    const string CampaignBank = "rgn_s27_r500";
    const string WhistleEvent = "play_droppod_incoming", EngineEvent = "play_sm_thunderhawk_engine", EngineStop = "stop_sm_thunderhawk_engine", GunEvent = "play_sm_heavy_bolter_fire_single", HitsEvent = "play_heavybolter_valkyrie_hits";
    /// <summary>A whistle starts this long before its shell lands (s, ours; every third shell has one).</summary>
    const float WhistleLead = 0.9f;
    /// <summary>The local whistle: optional, git-ignored and left out of exports (as the menu background): the project's, or beside the exported game. Without it the bank's stand-in plays.</summary>
    const string WhistleFile = "res://assets/musou/whistle.ogg";
    const int WhistlePlayers = 10;
    /// <summary>Its level on the sound bus (dB; ours, near the bank's whistle) and its reach (m).</summary>
    const float WhistleDb = -6f, WhistleReach = 160f;
    /// <summary>The sound's speed over ground (m/s): a far burst is heard and felt late.</summary>
    const float SoundSpeed = 340f;
    /// <summary>The shake (radians at 0 m, ours), how far it reaches (m), and the most it adds up to.</summary>
    const float ShakeAtZero = 0.05f, ShakeReach = 110f, ShakeCap = 0.07f;
    /// <summary>The Thunderhawk's rumble within this distance.</summary>
    const float FlyoverShakeReach = 70f;
    /// <summary>The war's level and low-pass (LPF units; ours): in a battle and, more muffled, inside the Stormbird.</summary>
    const float WarGainDb = -22f, WarLpf = 62f, HubGainDb = -28f, HubLpf = 82f;
    /// <summary>The emitter's distance round the listener (m; ours: its direction is what counts).</summary>
    const float WarNear = 22f, WarFar = 36f;
    /// <summary>A looping voice on a one-shot place is stopped after this long (s).</summary>
    const float WarLoopSeconds = 7f;
    /// <summary>Seconds between two of the war's sounds, drawn in this range (ours).</summary>
    const float WarGapMin = 0.5f, WarGapMax = 2.2f;
    /// <summary>Round the battle's points: the far fires and flak (m; ours).</summary>
    const float VistaRadius = 240f;
    const int VistaCount = 7;

    sealed record FireAssets(Dictionary<string, CSystem> Systems, Dictionary<string, byte[]> Decals, StaticModel? Aircraft, StaticModel? Gun, List<string> Notes);

    readonly List<TfwFireEvent> _tfwFireIn = new();
    readonly List<(TfwFireEvent Shell, bool Whistled)> _tfwShells = new();
    readonly List<(TfwFireEvent Round, Vector3 Target)> _tfwRoundsDue = new();
    readonly List<(double At, float Amount)> _tfwShocks = new();
    readonly AudioStreamPlayer3D?[] _tfwWhistles = new AudioStreamPlayer3D?[WhistlePlayers];
    AudioStreamOggVorbis? _tfwWhistle;
    bool _tfwWhistleRead;
    int _tfwWhistleNext;
    readonly List<(double At, SoundEmitter Emitter)> _tfwLoopStops = new();
    readonly Random _tfwRoll = new(77);
    Task<FireAssets>? _tfwFireLoad;
    bool _tfwFireLoaded, _tfwBanksLoaded, _tfwVista;
    TfwBlastCloud? _tfwCloud;
    Node3D? _tfwAircraft;
    TfwFireEvent? _tfwRun;
    SoundEmitter? _tfwRunEmitter;
    double _tfwRunLastGun, _tfwRunLastHit;
    FxEffect? _tfwJetA, _tfwJetB;
    string? _tfwFlyby;
    bool _tfwEngine;
    double _tfwWarNext;
    int _tfwShellsPlayed, _tfwRoundsPlayed, _tfwRunsPlayed, _tfwWarPlayed, _tfwWarSilent;
    float _tfwShakeTotal;
    readonly Dictionary<string, int> _tfwFirePosted = new();
    double _tfwFireLogAt;

    /// <summary>Takes this frame's records before the state forgets them (from <see cref="TfwGuardsFrame"/>).</summary>
    void TfwFireTake()
    {
        if (!Lean) _tfwFireIn.AddRange(_tfwState.NewFire);
    }

    /// <summary>The frame: the test aid, the assets, the records, the shells, the aircraft, the war.</summary>
    void TfwFireFrame(in TfwWarView war, double delta)
    {
        if (Lean || _client is null || _config is null) { _tfwFireIn.Clear(); return; }
        TfwFireTestFrame(war, delta);
        TfwOrbitalTestFrame(war);
        bool draw = DisplayServer.GetName() != "headless" && !_musouNoDraw;
        TfwFireAssets(draw);
        TfwFireBanks();
        double tick = _client.RemotePlayers().Tick;
        if (tick <= 0) tick = _tfwState.NewestTick;
        foreach (var f in _tfwFireIn)
            switch (f.Kind)
            {
                case TfwFireKind.Shell: _tfwShells.Add((f, false)); _tfwOrbitalSeen.Add(f); break;
                case TfwFireKind.Run: TfwRunBegins(f, tick); break;
                case TfwFireKind.Round: _tfwRoundsDue.Add((f, ToGodot(f.To))); break;
            }
        _tfwFireIn.Clear();
        int rate = Math.Max(1, _config.TickRate);
        TfwShellsFrame(tick, rate);
        TfwRoundsFrame(tick);
        TfwAircraftFrame(tick, rate, (float)delta);
        TfwShockFrame();
        _tfwCloud?.Frame((float)delta);
        TfwWarSoundFrame(war);
        TfwVistaFrame(war);
        if (_connectedFor >= _tfwFireLogAt && (_tfwShellsPlayed + _tfwRoundsPlayed + _tfwWarPlayed) > 0)
        {
            _tfwFireLogAt = _connectedFor + 10.0;
            GD.Print($"t {_connectedFor:F1} s: TFWFX shells {_tfwShellsPlayed}, runs {_tfwRunsPlayed}, rounds {_tfwRoundsPlayed}, war sounds {_tfwWarPlayed} (+{_tfwWarSilent} silent), flyby {_tfwFlyby ?? "none"}, shake total {_tfwShakeTotal:F3}, reduce motion {UiKit.ReduceMotion}, puffs {_tfwCloud?.Live ?? 0} ({_tfwCloud?.LastMs ?? 0:F2} ms), posted: {string.Join(", ", _tfwFirePosted.Select(kv => $"{kv.Key} {kv.Value}"))}");
        }
    }

    /// <summary>Starts the effects' and the aircraft's load once the effects player is up, and takes it when done (the Godot objects on this thread).</summary>
    void TfwFireAssets(bool draw)
    {
        if (_tfwFireLoaded || !FxActive || _fx?.Library is not { } library) return;
        if (_tfwFireLoad is null)
        {
            string dir = _gameDir;
            _tfwFireLoad = Task.Run(() =>
            {
                var failures = new List<string>();
                var systems = CompileSystems(library, FxNames, failures);
                var decals = library.Textures(["metal_crater_dif"]);
                var notes = new List<string>(failures);
                StaticModel? aircraft = null, gun = null;
                if (TfwFireRules.RunsEnabled) try { aircraft = StaticModel.Load(Path.Combine(dir, "data", "chunks", "ebp_sets__s002-001.oppc"), "thunderhawk", notes, [Path.Combine(dir, "data", "chunks", "mp_tdm_17__r000_main.oppc")]); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { notes.Add("the aircraft does not load: " + ex.Message); }
                if (TfwFireRules.RunsEnabled) try { gun = StaticModel.Load(Path.Combine(dir, "data", "packages", "hbolter_thunderhawk_wings.oppc"), "hbolter_thunderhawk_wings", notes); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { notes.Add("the wing gun does not load: " + ex.Message); }
                return new FireAssets(systems, decals, aircraft, gun, notes);
            });
            return;
        }
        if (!_tfwFireLoad.IsCompleted) return;
        _tfwFireLoaded = true;
        if (_tfwFireLoad.IsFaulted) { GD.PrintErr("tfw fire: load failed: " + _tfwFireLoad.Exception?.GetBaseException().Message); return; }
        var a = _tfwFireLoad.Result;
        _fx.Preload(a.Systems, a.Decals);
        GD.Print($"tfw fire: {a.Systems.Count} of {FxNames.Length} effects, crater {(a.Decals.Count > 0 ? "yes" : "no")}, aircraft {(a.Aircraft is null ? "no" : a.Aircraft.MeshName)}");
        foreach (string note in a.Notes.Take(10)) GD.Print("  tfw fire note: " + note);
        if (!draw) return;
        _tfwCloud = new TfwBlastCloud { Name = "TfwBlastCloud" };
        AddChild(_tfwCloud);
        if (TfwFireRules.RunsEnabled) _tfwAircraft = TfwBuildAircraft(a.Aircraft, a.Gun);
    }

    /// <summary>The aircraft's node: the install's mesh with its guns on the wings (the place ours); a dull slab when the mesh does not load, so a run stays visible (placeholder).</summary>
    Node3D TfwBuildAircraft(StaticModel? model, StaticModel? gun)
    {
        var root = new Node3D { Name = "TfwThunderhawk", Visible = false };
        var textures = new Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        MeshInstance3D Mesh(StaticModel m)
        {
            var mesh = new ArrayMesh();
            var blocks = new Dictionary<int, Godot.Collections.Array>();
            foreach (var surface in m.Surfaces)
            {
                if (m.Mesh.Blocks[surface.BlockIndex] is not { } block || block.Positions.Length == 0 || surface.Range.IndexCount < 3) continue;
                if (!blocks.TryGetValue(surface.BlockIndex, out var shared)) blocks[surface.BlockIndex] = shared = ModelViewer.BlockArrays(block);
                var indices = new int[surface.Range.IndexCount - surface.Range.IndexCount % 3];
                for (int i = 0; i < indices.Length; i++) indices[i] = block.Indices[surface.Range.StartIndex + i];
                var arrays = shared.Duplicate();
                arrays[(int)Godot.Mesh.ArrayType.Index] = indices;
                mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, ModelViewer.MaterialFor(surface, textures, notes));
            }
            m.ReleaseTextures();
            for (int i = 0; i < mesh.GetSurfaceCount(); i++)
                if (mesh.SurfaceGetMaterial(i) is StandardMaterial3D skin) { skin.Roughness = 0.55f; skin.Metallic = 0.35f; skin.AlbedoColor = new Color(0.72f, 0.72f, 0.74f); }
            return new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        }
        if (model is not null) root.AddChild(Mesh(model));
        else root.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(28f, 6f, 30f) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.27f, 0.3f) } });
        if (gun is not null)
            foreach (float side in new[] { -1f, 1f })
            {
                var g = Mesh(gun);
                g.Position = new Vector3(side * TfwFireRules.GunSide, -TfwFireRules.GunBelow, 0f);
                root.AddChild(g);
            }
        foreach (string note in notes.Take(6)) GD.Print("  tfw fire note: " + note);
        AddChild(root);
        return root;
    }

    /// <summary>The banks, once the sound is up: <c>vehicles</c> (whistle, Valkyrie) and the campaign's for the flyby; then which flyby this install can sound.</summary>
    void TfwFireBanks()
    {
        if (_tfwBanksLoaded || _audio is null || _sound is null) return;
        _tfwBanksLoaded = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool vehicles = _audio.AddPackage(_gameDir, "vehicles"), campaign = TfwFireRules.RunsEnabled && _audio.AddPackage(_gameDir, CampaignBank);
        _tfwFlyby = FlybyEvents.FirstOrDefault(n => _audio.HasEvent(n) && _audio.Post(n, SoundSwitches.None).Voices.Count > 0);
        GD.Print($"tfw fire: banks vehicles {vehicles}, {CampaignBank} {campaign} ({watch.ElapsedMilliseconds} ms); flyby {_tfwFlyby ?? "none"}; " + string.Join(", ", new[] { WhistleEvent, EngineEvent, GunEvent, HitsEvent, BurstEvents[0], BurstEvents[1], FarBurstEvents[0], FarBurstEvents[1] }.Select(n => $"{n} {(_audio.HasEvent(n) ? _audio.Post(n, SoundSwitches.None).Voices.Count : -1)}")));
    }

    /// <summary>Posts an event at a place; whether a voice came of it. The count is the report on what played (nobody here can listen).</summary>
    bool TfwPost(string name, SoundEmitter emitter, float delay = 0f, VoiceOffset offset = default)
    {
        if (_sound?.Post(name, emitter, delay, offset: offset) is not { Found: true } play || play.Voices.Count == 0) return false;
        _tfwFirePosted[name] = _tfwFirePosted.GetValueOrDefault(name) + 1;
        if (emitter.OneShot && play.Voices.Exists(v => v.LoopCount == 0)) _tfwLoopStops.Add((_connectedFor + WarLoopSeconds, emitter));   // nothing else stops a looping voice on a one-shot place
        if (LogSound("tfw")) GD.Print($"t {_connectedFor:F1} s: TFWSOUND {name} {play.Voices.Count} voices, delay {delay:F2}, offset {offset}, at {emitter.Current}");
        return true;
    }

    bool TfwPostAt(string name, Vector3 at, float delay = 0f, VoiceOffset offset = default) => TfwPost(name, new SoundEmitter { Position = at, OneShot = true, Label = "tfw fire" }, delay, offset);

    float CameraDistance(Vector3 at) => _camera is { } c ? c.GlobalPosition.DistanceTo(at) : 0f;

    void TfwShellsFrame(double tick, int rate)
    {
        for (int k = _tfwShells.Count - 1; k >= 0; k--)
        {
            var (shell, whistled) = _tfwShells[k];
            var at = ToGodot(shell.At);
            if (!whistled && tick >= shell.Tick - WhistleLead * rate)
            {
                whistled = true;
                _tfwShells[k] = (shell, true);
                if (CameraDistance(at) < WhistleReach) TfwWhistle(at + Vector3.Up * 12f);
            }
            if (tick < shell.Tick) continue;
            _tfwShells.RemoveAt(k);
            if (tick - shell.Tick < rate) TfwBurst(at);
        }
    }

    /// <summary>The cue before an impact: the local file when there is one, its end on the impact; else the bank's stand-in.</summary>
    void TfwWhistle(Vector3 at)
    {
        if (!_tfwWhistleRead)
        {
            _tfwWhistleRead = true;
            string exeDir = OS.GetExecutablePath().GetBaseDir();
            foreach (string path in new[] { ProjectSettings.GlobalizePath(WhistleFile), Path.Combine(exeDir, "assets", "musou", "whistle.ogg") })
            {
                if (!File.Exists(path)) continue;
                _tfwWhistle = AudioStreamOggVorbis.LoadFromFile(path);
                if (_tfwWhistle is not null) { GD.Print($"tfw fire: the local whistle {Path.GetFileName(path)}, {_tfwWhistle.GetLength():F2} s"); break; }
            }
        }
        if (_tfwWhistle is null || _sound is null) { TfwPostAt(WhistleEvent, at, 0f, new VoiceOffset(GainDb: -3f)); return; }
        var player = _tfwWhistles[_tfwWhistleNext];
        if (player is null)
        {
            player = new AudioStreamPlayer3D { Stream = _tfwWhistle, Bus = SoundSystem.BusName, UnitSize = 30f, MaxDistance = WhistleReach, VolumeDb = WhistleDb + _sound.GlobalGainDb };
            AddChild(player);
            _tfwWhistles[_tfwWhistleNext] = player;
        }
        _tfwWhistleNext = (_tfwWhistleNext + 1) % WhistlePlayers;
        player.GlobalPosition = at;
        player.Play((float)Math.Max(0.0, _tfwWhistle.GetLength() - WhistleLead));
        _tfwFirePosted["local whistle"] = _tfwFirePosted.GetValueOrDefault("local whistle") + 1;
    }

    /// <summary>One shell's landing: the install's fire over <see cref="TfwBlastCloud"/>'s plume, a crater, the sound and the shock late by the distance.</summary>
    void TfwBurst(Vector3 at)
    {
        _tfwShellsPlayed++;
        if (_tfwFirstBurst < 0.0) _tfwFirstBurst = _connectedFor;
        float distance = CameraDistance(at);
        _tfwCloud?.Burst(at, distance < 90f ? 1f : distance < 180f ? 0.6f : 0.35f);
        if (_fx is { } fx)
        {
            var up = Basis.Identity;
            fx.Play("artillery_explosion", at + Vector3.Up * 0.3f, up, 1.2f, priority: 2);
            if (_tfwShellsPlayed % 3 == 0) fx.Play("oob_explosion_huge", at + Vector3.Up * 2f, up, 0.45f, lifetime: 1.6f, priority: 2);
            fx.PlaceDataDecal("metal_crater_dif", at + Vector3.Up * 0.1f, up, 11f, 11f, 3f, 40f, PoundRing.Fade);
        }
        float late = distance / SoundSpeed;
        if (distance < 130f)
        {
            float gain = distance < 40f ? 8f : 4f;
            TfwPostAt(BurstEvents[_tfwShellsPlayed % BurstEvents.Length], at, late, new VoiceOffset(GainDb: gain));
            if (distance < 45f && _tfwShellsPlayed % BurstEvents.Length != 0) TfwPostAt(BurstEvents[0], at, late, new VoiceOffset(GainDb: gain));
        }
        else TfwPostAt(FarBurstEvents[_tfwShellsPlayed % FarBurstEvents.Length], at, late);
        float k = Math.Clamp(1f - distance / ShakeReach, 0f, 1f);
        if (k > 0f) _tfwShocks.Add((_connectedFor + late, ShakeAtZero * k * MathF.Sqrt(k)));
    }

    /// <summary>The shock reaches the camera. Reduce motion: none at all.</summary>
    void TfwShockFrame()
    {
        for (int k = _tfwLoopStops.Count - 1; k >= 0; k--)
        {
            if (_connectedFor < _tfwLoopStops[k].At) continue;
            _sound?.StopEmitter(_tfwLoopStops[k].Emitter);
            _tfwLoopStops.RemoveAt(k);
        }
        for (int k = _tfwShocks.Count - 1; k >= 0; k--)
        {
            var (when, amount) = _tfwShocks[k];
            if (_connectedFor < when) continue;
            _tfwShocks.RemoveAt(k);
            TfwShake(amount);
        }
    }

    void TfwShake(float amount)
    {
        if (UiKit.ReduceMotion || !FxActive || _camera is null) return;
        float room = MathF.Max(0f, ShakeCap - _shake.X);
        amount = MathF.Min(amount, room);
        FxShake(amount);
        _tfwShakeTotal += amount;
    }

    void TfwRoundsFrame(double tick)
    {
        for (int k = _tfwRoundsDue.Count - 1; k >= 0; k--)
        {
            var (round, target) = _tfwRoundsDue[k];
            if (tick < round.Tick) continue;
            _tfwRoundsDue.RemoveAt(k);
            _tfwRoundsPlayed++;
            var from = TfwMuzzle(round.At, tick);
            var way = target - from;
            float length = way.Length();
            if (_fx is { } fx && length > 1f)
            {
                var dir = way / length;
                fx.Play("heavy_bolter_thunderhawk_tracer", from, AxesFromY(dir), 1f, target: target, priority: 1);
            }
            _tfwCloud?.Spit(target, false);
            if (_tfwRunEmitter is { } emitter && _connectedFor - _tfwRunLastGun > 0.09)
            {
                _tfwRunLastGun = _connectedFor;
                TfwPost(GunEvent, emitter);
            }
            if (_connectedFor - _tfwRunLastHit > 0.14 && CameraDistance(target) < 90f)
            {
                _tfwRunLastHit = _connectedFor;
                TfwPostAt(HitsEvent, target);
            }
        }
    }

    /// <summary>A tracer leaves the aircraft as drawn (the record's muzzle was the server's place at that tick).</summary>
    Vector3 TfwMuzzle(Num.Vector3 muzzle, double tick) => ToGodot(muzzle);

    void TfwRunBegins(in TfwFireEvent run, double tick)
    {
        _tfwRun = run;
        _tfwRunsPlayed++;
        _tfwRunEmitter = new SoundEmitter { Position = ToGodot(run.At), Label = "tfw thunderhawk", Follow = () => _tfwAircraft is { Visible: true } a ? a.GlobalPosition : null };
        if (_tfwFlyby is { } flyby) TfwPost(flyby, _tfwRunEmitter, 2.5f);   // the pass is timed round the aircraft's nearest point
        _tfwEngine = TfwPost(EngineEvent, _tfwRunEmitter);
        if (_fx is { } fx && _tfwAircraft is { } aircraft)
        {
            (Vector3, Basis)? Rear(float side) => aircraft.Visible ? (aircraft.GlobalTransform * new Vector3(side, 1f, 15f), aircraft.GlobalBasis) : null;
            _tfwJetA = fx.Play("thunderhawk_jet", aircraft.GlobalPosition, aircraft.GlobalBasis, 5f, follow: () => Rear(-3.5f), repeat: true, priority: 2, tint: new Color(1f, 0.72f, 0.45f));
            _tfwJetB = fx.Play("thunderhawk_jet", aircraft.GlobalPosition, aircraft.GlobalBasis, 5f, follow: () => Rear(3.5f), repeat: true, priority: 2, tint: new Color(1f, 0.72f, 0.45f));
        }
    }

    /// <summary>The aircraft flies its line from the one record, banking a little over the battlefield, and rumbles the camera as it passes.</summary>
    void TfwAircraftFrame(double tick, int rate, float delta)
    {
        if (_tfwRun is not { } run) return;
        var from = ToGodot(run.At);
        var way = ToGodot(run.To) - from;
        float length = way.Length(), flown = (float)((tick - run.Tick) / rate) * TfwFireRules.RunSpeed;
        if (flown >= length || length < 1f)
        {
            _tfwRun = null;
            if (_tfwAircraft is { } done) done.Visible = false;
            if (_tfwRunEmitter is { } emitter) { if (_tfwEngine) TfwPost(EngineStop, emitter); _sound?.StopEmitter(emitter); }   // the pass and the engine loop
            _tfwRunEmitter = null;
            _tfwEngine = false;
            if (_tfwJetA is not null) { _tfwJetA.Life = 0.0; _tfwJetA = null; }
            if (_tfwJetB is not null) { _tfwJetB.Life = 0.0; _tfwJetB = null; }
            return;
        }
        var dir = way / length;
        var at = from + dir * Math.Max(0f, flown);
        if (_tfwAircraft is { } aircraft)
        {
            aircraft.Visible = true;
            float bank = 0.14f * MathF.Sin(flown / length * MathF.Tau);
            aircraft.GlobalTransform = new Transform3D(Basis.LookingAt(dir, Vector3.Up) * new Basis(Vector3.Forward, bank), at);
        }
        float distance = CameraDistance(at);
        if (distance < FlyoverShakeReach) TfwShake(0.012f * (1f - distance / FlyoverShakeReach) * delta * 30f);
    }

    /// <summary>The war round the walls: a far sound every moment or so, muffled, from a bearing of its own; more muffled inside the Stormbird.</summary>
    void TfwWarSoundFrame(in TfwWarView war)
    {
        if (_sound is null || !_tfwBanksLoaded || _camera is not { } camera || _connectedFor < _tfwWarNext) return;
        _tfwWarNext = _connectedFor + WarGapMin + _tfwRoll.NextSingle() * (WarGapMax - WarGapMin);
        bool inside = war.Phase is TfwRunPhase.Hub or TfwRunPhase.Travel;
        float total = WarEvents.Sum(e => e.Weight), roll = _tfwRoll.NextSingle() * total;
        var pick = WarEvents[^1];
        foreach (var e in WarEvents) { if ((roll -= e.Weight) <= 0f) { pick = e; break; } }
        float bearing = _tfwRoll.NextSingle() * MathF.Tau, far = WarNear + _tfwRoll.NextSingle() * (WarFar - WarNear);
        var at = camera.GlobalPosition + new Vector3(MathF.Cos(bearing) * far, 6f, MathF.Sin(bearing) * far);
        var offset = new VoiceOffset(GainDb: (inside ? HubGainDb : WarGainDb) + pick.Gain, PitchCents: (_tfwRoll.NextSingle() - 0.5f) * 300f - 150f, Lpf: inside ? HubLpf : WarLpf);
        var emitter = new SoundEmitter { Position = at, OneShot = true, Label = "tfw war" };
        if (TfwPost(pick.Event, emitter, 0f, offset)) _tfwWarPlayed++;
        else _tfwWarSilent++;
    }

    /// <summary>Far fires and flak round the battlefield, once its points are known (they stand as long as the map does).</summary>
    void TfwVistaFrame(in TfwWarView war)
    {
        if (_tfwVista || _fx is not { } fx || !_tfwFireLoaded || _tfwCloud is null || _tfwState.Objectives.Count == 0) return;
        _tfwVista = true;
        var centre = Num.Vector3.Zero;
        foreach (var o in _tfwState.Objectives) centre += o.Position;
        centre /= _tfwState.Objectives.Count;
        var middle = ToGodot(centre);
        for (int k = 0; k < VistaCount; k++)
        {
            float a = k * MathF.Tau / VistaCount + 0.4f;
            var at = middle + new Vector3(MathF.Cos(a) * VistaRadius, 0f, MathF.Sin(a) * VistaRadius);
            fx.Play("oob_explosions_artillery_less", at, Basis.Identity, 2.2f, repeat: true, ambient: true);
            fx.Play("amb_anti_aircraft_fire_valkyries", at + Vector3.Up * 30f, Basis.Identity, 1.5f, repeat: true, ambient: true);
        }
    }

    // ---------------------------------------------------------------- test aids

    static readonly bool TfwFireTestOn = CmdFlag("tfw-fire-test"), TfwFireCam = CmdFlag("tfw-fire-cam"), TfwReduceMotionArg = CmdFlag("tfw-reduce-motion");
    double _tfwFireTestNext = -1, _tfwLandedAt = -1;
    bool _tfwF9;
    Vector3? _tfwCamEye, _tfwCamLook;

    /// <summary>See the class summary.</summary>
    void TfwFireTestFrame(in TfwWarView war, double delta)
    {
        if (TfwReduceMotionArg) UiKit.ReduceMotion = true;
        if (!TfwFireTestOn || _client is null) return;
        var me = _client.Predicted;
        bool battle = war.Phase is TfwRunPhase.Battle or TfwRunPhase.FinalAssault;
        if (!battle) { _tfwLandedAt = -1; return; }
        if (_tfwLandedAt < 0)
        {
            _tfwLandedAt = _connectedFor;
            _tfwFireTestNext = _connectedFor + (float.TryParse(CmdArg("tfw-fire-test"), System.Globalization.CultureInfo.InvariantCulture, out float wait) ? wait : 6f);
        }
        var ahead = new Num.Vector3(-MathF.Sin(me.Yaw), MathF.Cos(me.Yaw), 0f);
        var point = TfwTestPoint("tfw-fire-at") ?? me.Position + ahead * 35f;
        bool f9 = Input.IsKeyPressed(Key.F9);
        byte what = (byte)(f9 && !_tfwF9 ? 1 : 0);
        _tfwF9 = f9;
        if (_connectedFor >= _tfwFireTestNext)
        {
            what = 1;
            _tfwFireTestNext = _connectedFor + 30.0;
            if (TfwFireCam) { _tfwCamEye = ToGodot(TfwTestPoint("tfw-fire-cam") ?? me.Position - ahead * 30f + new Num.Vector3(0f, 0f, 38f)); _tfwCamLook = ToGodot(point + new Num.Vector3(0f, 0f, 8f)); }
        }
        if (what == 0) return;
        _tfwState.Commands.Issue(TfwCommand.FireTest, what, point);
        GD.Print($"t {_connectedFor:F1} s: TFWFIRE asked for a barrage through ({point.X:F0}, {point.Y:F0})");
    }

    /// <summary>A test flag's <c>x,y,z</c> (game space); null when absent or malformed.</summary>
    static Num.Vector3? TfwTestPoint(string flag)
    {
        var p = (CmdArg(flag) ?? "").Split(',');
        return p.Length == 3 && float.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out float x) && float.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out float y) && float.TryParse(p[2], System.Globalization.CultureInfo.InvariantCulture, out float z) ? new Num.Vector3(x, y, z) : null;
    }

    static readonly bool TfwOrbitalTestOn = CmdFlag("tfw-orbital-test");
    readonly List<TfwFireEvent> _tfwOrbitalSeen = new();
    int _tfwOrbitalStage, _tfwOrbitalFailed;
    double _tfwOrbitalAt = -1, _tfwFirstBurst = -1;
    Num.Vector3 _tfwOrbitalChosen;

    void TfwOrbitalCheck(string what, bool passed)
    {
        GD.Print($"TFWTEST {(passed ? "PASS" : "FAIL")}: orbital: {what}");
        if (!passed) _tfwOrbitalFailed++;
    }

    /// <summary>
    /// <c>--tfw-orbital-test</c> (a scripted player, not <c>--bot</c>, with <c>--tfw-deploy</c>): looks at the ground, presses the real key B, and checks that the shells start about 1 s later, centred on
    /// the point under the crosshair; that the HUD reads ready with no cooldown; and that the server takes a second call at once. Quits 0, or 1 when any failed.
    /// </summary>
    void TfwOrbitalTestFrame(in TfwWarView war)
    {
        if (!TfwOrbitalTestOn || _client is null || !TfwHudModel.InBattle(war.Phase)) return;
        var me = _client.Predicted;
        if (_tfwOrbitalAt < 0) { _tfwOrbitalAt = _connectedFor + 4.0; return; }
        if (_connectedFor < _tfwOrbitalAt) { if (_tfwOrbitalStage == 0) _pitch = -0.32f; return; }
        switch (_tfwOrbitalStage)
        {
            case 0:
                _tfwOrbitalChosen = TfwCrosshairPoint(me);
                GD.Print($"t {_connectedFor:F1} s: TFWORBITAL pressing B, crosshair on ({_tfwOrbitalChosen.X:F1}, {_tfwOrbitalChosen.Y:F1}, {_tfwOrbitalChosen.Z:F1}), player at ({me.Position.X:F1}, {me.Position.Y:F1})");
                _tfwOrbitalSeen.Clear();
                _tfwFirstBurst = -1;
                TfwKey(Key.B, true);
                _tfwOrbitalStage = 1; _tfwOrbitalAt = _connectedFor + 0.2;
                break;
            case 1:
                TfwKey(Key.B, false);
                _tfwOrbitalStage = 2; _tfwOrbitalAt = _connectedFor + 12.0;
                break;
            case 2:
                double first = _tfwFirstBurst - _tfwOrbitalCalled;
                TfwOrbitalCheck($"first impact {first:F2} s after the press", _tfwOrbitalCalled >= 0 && first is > 0.6 and < 1.8);
                TfwOrbitalCheck($"{_tfwOrbitalSeen.Count} shells", _tfwOrbitalSeen.Count is >= TfwFireRules.ShellsMin and <= TfwFireRules.ShellsMax);
                var centre = Num.Vector3.Zero;
                foreach (var s in _tfwOrbitalSeen) centre += s.At;
                centre /= Math.Max(1, _tfwOrbitalSeen.Count);
                float off = new Num.Vector2(centre.X - _tfwOrbitalChosen.X, centre.Y - _tfwOrbitalChosen.Y).Length();
                TfwOrbitalCheck($"the barrage's centre ({centre.X:F1}, {centre.Y:F1}) is {off:F1} m from the crosshair's point", off < 2f * TfwFireRules.Scatter);
                TfwOrbitalCheck($"no cooldown: {war.Own.OrbitalSeconds} s, {TfwHudModel.OrbitalText}", war.Own.OrbitalSeconds == 0);
                _tfwOrbitalSeen.Clear();
                _tfwState.Commands.Issue(TfwCommand.Orbital, 0, _tfwOrbitalChosen);
                _tfwOrbitalStage = 3; _tfwOrbitalAt = _connectedFor + 3.0;
                break;
            case 3:
                TfwOrbitalCheck("the server took a second call at once", _tfwOrbitalSeen.Count > 0);
                GD.Print($"TFWTEST orbital done: {_tfwOrbitalFailed} failed; posted: {string.Join(", ", _tfwFirePosted.Select(kv => $"{kv.Key} {kv.Value}"))}");
                GetTree().Quit(_tfwOrbitalFailed == 0 ? 0 : 1);
                _tfwOrbitalStage = 4;
                break;
        }
    }

    /// <summary><c>--tfw-fire-cam</c>: the camera stands where the first trigger put it, on the run while one flies. After the shake: a windowed capture's aid only.</summary>
    void TfwFireCamFrame()
    {
        if (!TfwFireCam || _camera is not { } camera || _tfwCamEye is not { } eye || _tfwCamLook is not { } look) return;
        var target = CmdFlag("tfw-fire-cam-run") && _tfwAircraft is { Visible: true } a ? a.GlobalPosition : look;
        camera.GlobalTransform = Transform3D.Identity.Translated(eye).LookingAt(target, Vector3.Up);
    }
}
