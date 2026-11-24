using System;
using Godot;

namespace SmClient.Extras.Weather;

/// <summary>
/// Rain motion (series 37) test aid: how many drops of each precipitation layer are live inside a sphere round the camera now, read from what the renderer was given (the layer's own instance buffer), for the
/// <c>--weather-density</c> log. Never used in play.
/// </summary>
public sealed partial class WeatherRig
{
    /// <summary>The sphere's radius round the camera (m) the density aid counts in.</summary>
    public const float DensityRadiusM = 8f;

    /// <summary>The live drops of the near rain, the far rain and the ash grit within <see cref="DensityRadiusM"/> of the camera.</summary>
    public (int Near, int Far, int Grit) DensityAround()
    {
        var c = GlobalPosition;
        return (Live(_rain, c), Live(_rainFar, c), Live(_ashMain, c));
    }

    static int Live(GeometryInstance3D? layer, Vector3 c)
    {
        if (layer is null || !layer.Visible) return 0;
        if (layer is WeatherField f) return f.CountWithin(c, DensityRadiusM);   // the field's drops by the shader's own arithmetic
        if (layer is CpuParticles3D p)
        {
            if (!p.Emitting && !p.IsInsideTree()) return 0;
            var buf = RenderingServer.MultimeshGetBuffer(p.GetBase());
            int n = RenderingServer.MultimeshGetInstanceCount(p.GetBase());
            if (n <= 0 || buf.Length < n * 12) return 0;
            int stride = buf.Length / n, live = 0;
            float r2 = DensityRadiusM * DensityRadiusM;
            var xf = p.GlobalTransform;   // Godot 4 writes a world-space emitter's particles in the node's own frame at draw time
            for (int i = 0; i < n; i++)
            {
                int o = i * stride;
                if (buf[o] == 0f && buf[o + 1] == 0f && buf[o + 2] == 0f && buf[o + 4] == 0f && buf[o + 5] == 0f && buf[o + 6] == 0f) continue;   // an inactive particle has a zero basis
                var at = xf * new Vector3(buf[o + 3], buf[o + 7], buf[o + 11]);
                if (at.DistanceSquaredTo(c) <= r2) live++;
            }
            return live;
        }
        return 0;
    }
}
