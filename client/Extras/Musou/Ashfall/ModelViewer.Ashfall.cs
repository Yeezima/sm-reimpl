using System.Collections.Generic;

namespace SmClient;

public partial class ModelViewer
{
    /// <summary><c>--warboss-test</c>: the Musou Warboss model (<see cref="WarbossTestView"/>). <c>--ashfall-kit-gallery</c> (also with <c>--ashfall-dress</c> or <c>--ashfall-dense</c>): the Ashfall kit test view (<see cref="AshfallKitGallery"/>).</summary>
    partial void ExtrasTestAid(Dictionary<string, string?> args, ref bool handled)
    {
        if (args.ContainsKey("warboss-test"))   // the Musou Warboss model alone (WarbossTestView; it needs the install)
        {
            var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("res://"), ".."));
            var found = SmContent.GameInstall.TryLocate(args.GetValueOrDefault("game-dir"), repoRoot) ?? throw new System.IO.DirectoryNotFoundException(SmContent.GameInstall.NotFoundMessage);
            AddChild(new WarbossTestView(found.Dir, args) { Name = "WarbossTest" });
            handled = true;
            return;
        }
        if (args.ContainsKey("musou-crowd-test"))   // the Musou crowd's looks running and dying on a stage (MusouCrowdTestView; it needs the install)
        {
            var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("res://"), ".."));
            var found = SmContent.GameInstall.TryLocate(args.GetValueOrDefault("game-dir"), repoRoot) ?? throw new System.IO.DirectoryNotFoundException(SmContent.GameInstall.NotFoundMessage);
            AddChild(new MusouCrowdTestView(found.Dir, args) { Name = "MusouCrowdTest" });
            handled = true;
            return;
        }
        if (args.ContainsKey("mek-gallery"))   // expansion S4: the contraptions' SM1 models (Meks/MekGalleryView.cs; it needs the install)
        {
            var repoRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(Godot.ProjectSettings.GlobalizePath("res://"), ".."));
            var found = SmContent.GameInstall.TryLocate(args.GetValueOrDefault("game-dir"), repoRoot) ?? throw new System.IO.DirectoryNotFoundException(SmContent.GameInstall.NotFoundMessage);
            AddChild(new MekGalleryView(found.Dir, args.GetValueOrDefault("out")) { Name = "MekGallery" });
            handled = true;
            return;
        }
        if (!args.ContainsKey("ashfall-kit-gallery")) return;
        AddChild(new AshfallKitGallery(args) { Name = "AshfallKitGallery" });
        handled = true;
    }
}
