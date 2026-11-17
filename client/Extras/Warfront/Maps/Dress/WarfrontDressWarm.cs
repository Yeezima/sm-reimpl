using Godot;
using SmClient.Extras.Warfront.Dress;
using SmSim.Extras.Warfront.Maps;
using SmContent.Extras.Warfront;
using SmContent.Extras.Warfront.Maps;

namespace SmClient;

/// <summary>
/// The pre-warm of the plating shader (dressing second pass, ruling 2 of review 1): its first draw costs a pipeline compile of 1.4 to 2.5 s on a cold shader cache, which used to land in a dressing step's frame. When a Stage section (Preparing or Go) names a
/// Warfront map, once per process, the one process-wide shader (<see cref="WarfrontDressBake.Plated"/>) is drawn on two centimetre-sized meshes in front of the camera for a few frames (both vertex layouts the dressing uses: with and without a vertex colour; shadow casting
/// on), behind the travel screen, and then freed. The compile then happens in these frames, which the log names (<c>dress warm-up</c>), and not in the drawing. Called from <c>WarfrontWarmFrame</c> (NetworkView.WarfrontStage.cs). Client only; a failure leaves the
/// first draw to compile as before. <c>SM_DRESS_SHADER_SALT=x</c> (test aid) puts x into the shader's source so a cold cache can be tested without clearing the shared cache.
/// </summary>
public partial class NetworkView
{
    static bool _dressWarmed;
    Node3D? _dressWarmNode;
    int _dressWarmFrames;
    long _dressWarmLastUs;
    double _dressWarmWorst;

    /// <summary>Once a frame while a Stage section is up: starts the warm-up the first time a Warfront map is named, and runs its frames.</summary>
    void WarfrontDressWarmFrame(string map)
    {
        if (_dressWarmNode is not null)
        {
            if (!IsInstanceValid(_dressWarmNode)) { _dressWarmNode = null; return; }
            long now = (long)Time.GetTicksUsec();
            double ms = (now - _dressWarmLastUs) / 1000.0; _dressWarmLastUs = now;
            _dressWarmWorst = System.Math.Max(_dressWarmWorst, ms);
            GD.Print($"warfront: dress warm-up frame {_dressWarmFrames}: {ms:F0} ms (real time)");
            if (++_dressWarmFrames >= 4) { _dressWarmNode.QueueFree(); _dressWarmNode = null; GD.Print($"warfront: dress warm-up done; its longest frame {_dressWarmWorst:F0} ms (the plating shader is compiled behind the travel screen)"); }
            return;
        }
        if (_dressWarmed || _camera is null || WarfrontFarMassing.Off) return;
        if (WarfrontSlots.ByName(map) is null && map != WarfrontMaps.HqName && map != WarfrontMaps.FortressName) return;
        _dressWarmed = true;
        try
        {
            var host = new Node3D { Name = "DressWarm", Position = new Vector3(0f, 0f, -0.6f) };
            var tri = new System.Numerics.Vector3[] { new(0f, 0f, 0f), new(0.02f, 0f, 0f), new(0f, 0.02f, 0f) };
            host.AddChild(new MeshInstance3D { Name = "Plain", Mesh = AshfallView.MeshOf(tri), MaterialOverride = WarfrontDressBake.Plated(0), CastShadow = GeometryInstance3D.ShadowCastingSetting.On, Position = new Vector3(-0.02f, 0f, 0f) });
            var soup = new WarfrontDressBake.Soup();
            soup.Tri(new(0f, 0f, 0f), new(0.02f, 0f, 0f), new(0f, 0.02f, 0f), new(0f, 0f, -1f), Colors.White);
            host.AddChild(new MeshInstance3D { Name = "Coloured", Mesh = soup.ToMesh(), MaterialOverride = WarfrontDressBake.Plated(8), CastShadow = GeometryInstance3D.ShadowCastingSetting.On, Position = new Vector3(0.02f, 0f, 0f) });
            _camera.AddChild(host);
            _dressWarmNode = host; _dressWarmFrames = 0; _dressWarmWorst = 0; _dressWarmLastUs = (long)Time.GetTicksUsec();
            GD.Print($"warfront: dress warm-up started for {map}: the plating shader is drawn on two small meshes in front of the camera for 4 frames");
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.OutOfMemoryException))
        {
            GD.Print($"warfront: dress warm-up failed ({e.GetType().Name}: {e.Message}); the first draw compiles as before");
        }
    }
}
