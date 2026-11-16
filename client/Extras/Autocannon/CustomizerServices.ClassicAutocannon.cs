using System;
using System.Collections.Generic;
using SmContent.Extras.Autocannon;
using SmContent.Extras.Musou.Arsenal;
using SmSim;

namespace SmClient.Customizer;

/// <summary>
/// Classic PvP's autocannon in the customizer (<see cref="ContentExtras.Autocannon"/>, the extended profile; the owner, 2026-11-11): the front end plans a Join with the data's configuration, which has no
/// autocannon, so a Devastator or Havoc slot that carries it would send the default heavy weapon. Under a Classic profile with the flag (never Musou's, which has its own configuration) the configuration
/// is the data's with Classic's autocannon appended as the match tuning appends it (<see cref="ClassicAutocannon.WithAutocannon"/>: at the end of both heavy classes' lists, so the index is the server's).
/// The install twin it is built from is read once at the end of the services' load (on its worker, <see cref="ClassicAutocannonLoaded"/>); a read that fails leaves the data's configuration (and a
/// note). The in-match picker (the match's own configuration has it already) and every other profile are untouched; the Faithful page never has it, so a saved autocannon slot joins there with the
/// class's default heavy weapon (as it shows it).
/// </summary>
public sealed partial class CustomizerServices
{
    WeaponSpec? _classicAutocannonSource;
    bool _classicAutocannonRead;
    readonly object _classicAutocannonLock = new();
    (SimConfig Data, SimConfig Classic)? _classicAutocannonConfig;

    void ClassicAutocannonConfig(ref SimConfig? config)
    {
        var content = Content;
        if (config is null || InMatch || MadeForMatch || content.HasFlag(ContentExtras.Musou) || !content.HasFlag(ContentExtras.Autocannon)) return;
        WeaponSpec? source;
        lock (_classicAutocannonLock)
        {
            if (_classicAutocannonConfig is { } known && ReferenceEquals(known.Data, config)) { config = known.Classic; return; }
            source = _classicAutocannonSource;
        }
        if (source is null) return;   // not read (yet, or it failed): the data's configuration
        var notes = new List<string>();
        var classic = ClassicAutocannon.WithAutocannon(config, source, notes);
        lock (_classicAutocannonLock) _classicAutocannonConfig = (config, classic);
        config = classic;
    }

    /// <summary>The end of the load (its worker): reads the install twin the Classic autocannon is built from, once, in a build with added content.</summary>
    void ClassicAutocannonLoaded()
    {
        lock (_classicAutocannonLock) if (_classicAutocannonRead) return;
        var notes = new List<string>();
        WeaponSpec? source = null;
        try { source = Arsenal.ReadAutocannonSource(GameDir, notes, ClassicAutocannon.Who); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { notes.Add($"{ClassicAutocannon.Who} customizer config: {ex.Message}; the data's configuration is used"); }
        lock (_classicAutocannonLock) { _classicAutocannonSource = source; _classicAutocannonRead = true; }
        lock (Notes) Notes.AddRange(notes);
    }
}
