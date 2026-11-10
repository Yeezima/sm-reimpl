using System;
using System.Collections.Generic;
using System.Linq;
using SmContent;
using SmContent.Customization;

namespace SmClient.Customizer.Model;

/// <summary>
/// The profile being edited by the front end (engine-free; docs/customizer-design.md F4): the profile as saved, the working copy the screens edit, the
/// dirty flag (the working copy differs from what is on disk, compared through <see cref="ProfileStore.Serialize"/>) and Save / Revert. The creator and the
/// appearance editor share one session, so "unsaved" covers both. Nothing here touches Godot types; tests use a temp directory.
/// </summary>
/// <remarks>
/// musou/loadout-slots (2026-11-10): a <see cref="IProfileView"/> (an added mode's own loadout slots, set by the services for the content profile) makes <see cref="Working"/> and <see cref="Saved"/>
/// that view of the stored profile; edits are folded back into it (<see cref="IProfileView.Keep"/>), and Save writes the stored profile whole. Without a view (Classic PvP) the screens edit the stored
/// profile itself, as before. The shown profile is cached per stored profile, so an untouched <see cref="Working"/> is the same object between reads.
/// </remarks>
public sealed class ProfileSession
{
    string _savedText;
    CustomizationProfile _stored, _savedStored;
    IProfileView? _view;
    (CustomizationProfile Stored, CustomizationProfile Shown)? _workingShown, _savedShown;

    public ProfileSession(CustomizationCatalog catalog, ProfileStore store, ProfileLoadResult load)
    {
        Catalog = catalog; Store = store;
        _savedStored = _stored = load.Profile;
        Status = load.Status;
        Repairs = load.Repairs;
        _savedText = ProfileStore.Serialize(_savedStored);
    }

    /// <summary>Loads (and repairs) the profile from the store. A repaired profile is not written until the player saves.</summary>
    public static ProfileSession Open(CustomizationCatalog catalog, ProfileStore store) => new(catalog, store, store.Load(catalog));

    public CustomizationCatalog Catalog { get; }
    public ProfileStore Store { get; }

    /// <summary>The saved profile as the screens see it (the view's, when there is one).</summary>
    public CustomizationProfile Saved => Shown(_savedStored, ref _savedShown);

    /// <summary>The working profile as the screens see it (the view's, when there is one).</summary>
    public CustomizationProfile Working => Shown(_stored, ref _workingShown);

    /// <summary>The whole working profile as it is written (every loadout set, whatever the view shows).</summary>
    public CustomizationProfile Stored => _stored;

    /// <summary>The view in use, or null (the screens edit the stored profile).</summary>
    public IProfileView? View => _view;

    public ProfileLoadStatus Status { get; }
    /// <summary>What loading repaired or noted (shown once as a notice: design F8).</summary>
    public IReadOnlyList<ProfileRepair> Repairs { get; }

    /// <summary>The working copy differs from the saved profile.</summary>
    public bool Dirty => ProfileStore.Serialize(_stored) != _savedText;
    /// <summary>The file is of a newer schema or unreadable: edits cannot be saved.</summary>
    public bool ReadOnly => _stored.ReadOnly;

    /// <summary>Raised after every edit, revert, save and change of view.</summary>
    public event Action? Changed;

    CustomizationProfile Shown(CustomizationProfile stored, ref (CustomizationProfile Stored, CustomizationProfile Shown)? cache)
    {
        if (_view is null) return stored;
        if (cache is { } c && ReferenceEquals(c.Stored, stored)) return c.Shown;
        var shown = _view.Show(stored);
        cache = (stored, shown);
        return shown;
    }

    /// <summary>
    /// Shows the screens <paramref name="view"/> of the profile from now on (null: the stored profile itself). A new view first makes what it needs in the stored profile
    /// (<see cref="IProfileView.Open"/>) in the saved and the working copy alike, so nothing reads as unsaved.
    /// </summary>
    public void UseView(IProfileView? view)
    {
        if (ReferenceEquals(view, _view)) return;
        _view = view;
        _workingShown = _savedShown = null;
        if (view is not null)
        {
            bool clean = !Dirty;
            var opened = view.Open(_savedStored);
            if (!ReferenceEquals(opened, _savedStored))
            {
                _savedStored = opened;
                _savedText = ProfileStore.Serialize(_savedStored);
            }
            _stored = clean ? _savedStored : view.Open(_stored);   // the same making on both sides: a clean profile stays clean
        }
        Changed?.Invoke();
    }

    public void Edit(Func<CustomizationProfile, CustomizationProfile> change)
    {
        if (_view is null) _stored = change(_stored);
        else
        {
            var shown = Working;
            var next = change(shown);
            if (!ReferenceEquals(next, shown)) _stored = _view.Keep(_stored, next);
        }
        Changed?.Invoke();
    }

    /// <summary>Makes <paramref name="profile"/> (a whole stored profile) both the saved and the working copy without writing it (demo data for screenshots and tests).</summary>
    public void Seed(CustomizationProfile profile)
    {
        _savedStored = _stored = _view?.Open(profile) ?? profile;
        _workingShown = _savedShown = null;
        _savedText = ProfileStore.Serialize(_savedStored);
        Changed?.Invoke();
    }

    /// <summary>Drops the unsaved edits.</summary>
    public void Revert()
    {
        _stored = _savedStored;
        Changed?.Invoke();
    }

    /// <summary>Writes the working copy; returns null on success or the reason it failed (shown in the status line).</summary>
    public string? Save()
    {
        try
        {
            Store.Save(_stored);
            _savedStored = _stored;
            _savedText = ProfileStore.Serialize(_savedStored);
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>The notices to show once after loading: the non-informational repairs and a corrupt or newer file.</summary>
    public IReadOnlyList<string> Notices()
    {
        var notices = new List<string>();
        switch (Status)
        {
            case ProfileLoadStatus.Corrupt: notices.Add("Your saved loadouts could not be read. The old file was kept next to it and you start fresh."); break;
            case ProfileLoadStatus.NewerSchema: notices.Add("Your saved loadouts are from a newer version. They are shown read-only and not overwritten."); break;
            case ProfileLoadStatus.Unreadable: notices.Add("Your saved loadouts could not be opened. Changes cannot be saved."); break;
        }
        int repaired = Repairs.Count(r => !r.Informational);
        if (repaired > 0 && Status is ProfileLoadStatus.Loaded or ProfileLoadStatus.NewerSchema)
            notices.Add($"{repaired} unknown or outdated setting{(repaired == 1 ? "" : "s")} in your saved loadouts {(repaired == 1 ? "was" : "were")} reset to the default.");
        return notices;
    }
}
