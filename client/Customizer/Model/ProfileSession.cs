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
public sealed class ProfileSession
{
    string _savedText;

    public ProfileSession(CustomizationCatalog catalog, ProfileStore store, ProfileLoadResult load)
    {
        Catalog = catalog; Store = store;
        Saved = Working = load.Profile;
        Status = load.Status;
        Repairs = load.Repairs;
        _savedText = ProfileStore.Serialize(Saved);
    }

    /// <summary>Loads (and repairs) the profile from the store. A repaired profile is not written until the player saves.</summary>
    public static ProfileSession Open(CustomizationCatalog catalog, ProfileStore store) => new(catalog, store, store.Load(catalog));

    public CustomizationCatalog Catalog { get; }
    public ProfileStore Store { get; }
    public CustomizationProfile Saved { get; private set; }
    public CustomizationProfile Working { get; private set; }
    public ProfileLoadStatus Status { get; }
    /// <summary>What loading repaired or noted (shown once as a notice: design F8).</summary>
    public IReadOnlyList<ProfileRepair> Repairs { get; }

    /// <summary>The working copy differs from the saved profile.</summary>
    public bool Dirty => ProfileStore.Serialize(Working) != _savedText;
    /// <summary>The file is of a newer schema or unreadable: edits cannot be saved.</summary>
    public bool ReadOnly => Working.ReadOnly;

    /// <summary>Raised after every edit, revert and save.</summary>
    public event Action? Changed;

    public void Edit(Func<CustomizationProfile, CustomizationProfile> change)
    {
        Working = change(Working);
        Changed?.Invoke();
    }

    /// <summary>Makes <paramref name="profile"/> both the saved and the working copy without writing it (demo data for screenshots and tests).</summary>
    public void Seed(CustomizationProfile profile)
    {
        Saved = Working = profile;
        _savedText = ProfileStore.Serialize(Saved);
        Changed?.Invoke();
    }

    /// <summary>Drops the unsaved edits.</summary>
    public void Revert()
    {
        Working = Saved;
        Changed?.Invoke();
    }

    /// <summary>Writes the working copy; returns null on success or the reason it failed (shown in the status line).</summary>
    public string? Save()
    {
        try
        {
            Store.Save(Working);
            Saved = Working;
            _savedText = ProfileStore.Serialize(Saved);
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
