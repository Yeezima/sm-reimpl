using Godot;
using SmContent;
using SmSim;
using SmSim.Extras.Musou;

namespace SmClient;

public partial class NetworkView
{
    /// <summary>A Librarian's sword glows as <see cref="ForceBlade"/> (Musou Mode only).</summary>
    partial void ExtrasSwordOverlay(PlayerState s, WeaponModel model, ref ShaderMaterial? overlay)
    {
        if (MusouActive && _config is not null && LibrarianRules.IsLibrarian(_config, s.Class)) overlay = ForceBlade.OverlayFor(model);
    }
}
