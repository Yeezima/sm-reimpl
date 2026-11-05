using System.Collections.Generic;

namespace SmClient;

public partial class ModelViewer
{
    /// <summary><c>--ashfall-kit-gallery</c> (also with <c>--ashfall-dress</c> or <c>--ashfall-dense</c>): the Ashfall kit test view (<see cref="AshfallKitGallery"/>).</summary>
    partial void ExtrasTestAid(Dictionary<string, string?> args, ref bool handled)
    {
        if (!args.ContainsKey("ashfall-kit-gallery")) return;
        AddChild(new AshfallKitGallery(args) { Name = "AshfallKitGallery" });
        handled = true;
    }
}
