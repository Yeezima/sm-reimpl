using System;
using Godot;

namespace SmClient;

/// <summary>
/// What lies outside the hub (owner's item 29): black space with small dim white stars and the planet far below the open ramp, a huge curved surface with a bright atmosphere rim. It is a sky shader on an
/// <see cref="Godot.Environment"/> that the camera carries while the player is aboard (so the map's fog, sky and sun never reach the hub, and the hub's choices never reach the battle).
/// The planet's picture is <c>assets/musou/planet.jpg</c> (the owner's own image: git-ignored, left out of exports; read as a file from the project or beside the game); without it the shader paints a
/// brown planet of its own (placeholder demonstration). The stars and that fallback are procedural. Nothing here comes from the install.
/// </summary>
public static class TfwHubSky
{
    /// <summary>Where the planet's image is read from: the project in a development run, <c>assets/musou</c> beside the game in an exported one.</summary>
    public const string PlanetPath = "res://assets/musou/planet.jpg";

    /// <summary>The planet's angular radius, and how far its centre lies below the horizon looking north (the ramp's way): the limb then crosses the open door a little above the horizon (ours, from the references).</summary>
    public const float PlanetRadius = 1.0f, PlanetBelowHorizon = 0.93f;
    /// <summary>With the owner's picture: its centre this far below the horizon and turned left of the ramp's axis (radians), spanning <see cref="ImageSpan"/> degrees across.</summary>
    public const float ImagePitch = 0.26f, ImageTurn = 0.44f, ImageSpan = 90f;

    /// <summary>Whether the last <see cref="Build"/> found the owner's image.</summary>
    public static bool UsedImage { get; private set; }

    static Texture2D? _planet;
    static bool _planetRead;

    /// <summary>The image once, or null (missing, unreadable): the first of the project's path and the one beside the game that holds a decodable file.</summary>
    public static Texture2D? PlanetImage()
    {
        if (_planetRead) return _planet;
        _planetRead = true;
        try
        {
            string exeDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
            string project = System.IO.Path.GetDirectoryName(ProjectSettings.GlobalizePath(PlanetPath)) ?? "";
            string beside = System.IO.Path.Combine(exeDir, "assets", "musou");
            foreach (string folder in new[] { project, beside })
                foreach (string extension in new[] { "jpg", "jpeg", "png", "webp" })   // planet.jpg is the name; the other three are accepted so the owner's picture needs no conversion
                {
                    string path = System.IO.Path.Combine(folder, "planet." + extension);
                    if (!System.IO.File.Exists(path)) continue;
                    var image = Image.LoadFromFile(path);
                    if (image is null || image.IsEmpty()) continue;
                    image.GenerateMipmaps();
                    _planet = ImageTexture.CreateFromImage(image);
                    return _planet;
                }
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException or NotSupportedException) { GD.PrintErr("hub planet: " + e.Message); }
        return _planet;
    }

    /// <summary>The environment the camera carries aboard: the sky above, a dim warm ambient, no fog, no glow, a filmic tone.</summary>
    public static Godot.Environment Build()
    {
        var image = PlanetImage();
        UsedImage = image is not null;
        var material = new ShaderMaterial { Shader = new Shader { Code = Code } };
        // Game space (north is +Y, up is +Z) to the world's (x, z, -y).
        static Vector3 World(float x, float y, float z) => new(x, z, -y);
        // The ramp looks south (-Y). Without the picture the planet is a sphere; with it, the picture hangs as a flat far backdrop, turned a little to the left so its city stands in the door.
        var toPlanet = image is null ? World(0f, -MathF.Cos(PlanetBelowHorizon), -MathF.Sin(PlanetBelowHorizon))
            : World(MathF.Cos(ImagePitch) * MathF.Sin(ImageTurn), -MathF.Cos(ImagePitch) * MathF.Cos(ImageTurn), -MathF.Sin(ImagePitch));
        material.SetShaderParameter("image_span", ImageSpan * MathF.PI / 180f);
        material.SetShaderParameter("planet_dir", toPlanet.Normalized());
        material.SetShaderParameter("planet_up", Vector3.Up);
        material.SetShaderParameter("planet_radius", PlanetRadius);
        material.SetShaderParameter("use_image", image is not null);
        material.SetShaderParameter("rim_light", World(-0.6f, -0.2f, 0.8f).Normalized());
        if (image is not null) material.SetShaderParameter("planet_image", image);
        return new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = material, RadianceSize = Sky.RadianceSizeEnum.Size32 },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.62f, 0.55f, 0.48f),
            AmbientLightEnergy = 0.55f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            TonemapExposure = 1.15f,
            FogEnabled = false,
            VolumetricFogEnabled = false,
            GlowEnabled = false,
            SsaoEnabled = false,
        };
    }

    const string Code = """
        shader_type sky;

        uniform sampler2D planet_image : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
        uniform bool use_image = false;
        uniform vec3 planet_dir = vec3(0.0, -0.8, -0.6);
        uniform vec3 planet_up = vec3(0.0, 1.0, 0.0);
        uniform float planet_radius = 1.0;
        uniform float image_span = 2.6;
        uniform vec3 rim_light = vec3(0.5, 0.6, -0.6);

        float hash31(vec3 p) {
            p = fract(p * 0.3183099 + vec3(0.71, 0.113, 0.419));
            p *= 17.0;
            return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
        }

        float vnoise(vec3 p) {
            vec3 i = floor(p);
            vec3 f = fract(p);
            f = f * f * (3.0 - 2.0 * f);
            float a = mix(mix(hash31(i), hash31(i + vec3(1, 0, 0)), f.x), mix(hash31(i + vec3(0, 1, 0)), hash31(i + vec3(1, 1, 0)), f.x), f.y);
            float b = mix(mix(hash31(i + vec3(0, 0, 1)), hash31(i + vec3(1, 0, 1)), f.x), mix(hash31(i + vec3(0, 1, 1)), hash31(i + vec3(1, 1, 1)), f.x), f.y);
            return mix(a, b, f.z);
        }

        float fbm(vec3 p) {
            float v = 0.0;
            float a = 0.5;
            for (int k = 0; k < 5; k++) { v += a * vnoise(p); p = p * 2.03 + 7.1; a *= 0.5; }
            return v;
        }

        // Small dim white stars on black: two scales of a lattice over the sphere, one star at most per cell.
        vec3 stars(vec3 d) {
            vec3 col = vec3(0.0);
            for (int layer = 0; layer < 2; layer++) {
                float scale = layer == 0 ? 90.0 : 200.0;
                vec3 id = floor(d * scale);
                float h = hash31(id + float(layer) * 31.7);
                float density = layer == 0 ? 0.80 : 0.72;
                if (h > density) {
                    vec3 c = vec3(0.3) + 0.4 * vec3(hash31(id + 1.3), hash31(id + 2.7), hash31(id + 5.1));
                    vec3 s = normalize(id + c);
                    float dist = length(d - s) * scale;
                    float radius = layer == 0 ? 0.16 : 0.26;
                    float body = smoothstep(radius, radius * 0.25, dist);
                    float level = (layer == 0 ? 0.42 : 0.22) * (0.25 + 0.75 * hash31(id + 9.9));
                    vec3 tint = mix(vec3(0.85, 0.9, 1.0), vec3(1.0, 0.94, 0.86), hash31(id + 4.2));
                    col += tint * body * level;
                }
            }
            return col;
        }

        void sky() {
            vec3 d = normalize(EYEDIR);
            vec3 col = stars(d);
            if (use_image) {
                // The picture as a flat far backdrop in its own framing (a gnomonic view: straight lines stay straight, no warping), black space and stars beyond its edges.
                vec3 f = normalize(planet_dir);
                float k = dot(d, f);
                {
                    vec3 r = normalize(cross(planet_up, f));
                    vec3 u = cross(f, r);
                    vec2 size = vec2(textureSize(planet_image, 0));
                    float span_y = image_span * size.y / size.x;
                    float dr = dot(d, r);
                    float az = atan(dr, k);                                   // an angular view of the picture: its own framing, straight enough at this span
                    float el = atan(dot(d, u), length(vec2(dr, k)));
                    vec2 uv = vec2(0.5 + az / image_span, 0.5 - el / span_y);
                    // Left and below the picture the planet goes on (the picture's dark corners mirrored); above and to the right it ends in space, as the picture does at its rim.
                    if (uv.x < 0.0) uv.x = min(-uv.x, 1.0);
                    if (uv.y > 1.0) uv.y = max(2.0 - uv.y, 0.0);
                    if (uv.x < 1.0 && uv.y > 0.0) {
                        float edge = smoothstep(0.0, 0.06, min(1.0 - uv.x, uv.y));
                        vec3 pic = texture(planet_image, uv).rgb;
                        col = mix(col, pic, edge);
                    }
                }
                COLOR = col;
            } else {
            float cosA = clamp(dot(d, planet_dir), -1.0, 1.0);
            float ang = acos(cosA);
            float R = planet_radius;
            vec3 right = normalize(cross(planet_up, planet_dir));
            vec3 up = cross(planet_dir, right);
            vec3 sunwards = normalize(rim_light + planet_dir * 0.7);
            vec3 atmosphere = vec3(0.55, 0.72, 1.0);
            if (ang < R) {
                float sinR = sin(R);
                float sinT = sqrt(max(1.0 - cosA * cosA, 0.0));
                float t = cosA - sqrt(max(sinR * sinR - sinT * sinT, 0.0));
                vec3 n = (d * t - planet_dir) / sinR;                // the surface normal, outward
                vec2 plane = vec2(dot(n, right), dot(n, up));
                vec3 albedo;
                if (use_image) {
                    vec2 size = vec2(textureSize(planet_image, 0));
                    float s = 0.5 * 1.25 * min(size.x, size.y);        // the picture covers 1.25 of the disc's width across its shorter side, mirrored beyond
                    vec2 uv = vec2(0.5) + plane * s / size * vec2(1.0, -1.0);
                    uv = abs(mod(uv + 1.0, 2.0) - 1.0);                 // mirrored tiling, no seam
                    albedo = textureLod(planet_image, uv, 1.0).rgb;
                } else {
                    vec3 q = n * 3.0 + vec3(2.1, 5.7, 1.3);
                    float f = fbm(q);
                    float g = fbm(q * 2.6 + 11.0);
                    albedo = mix(vec3(0.30, 0.19, 0.10), vec3(0.66, 0.50, 0.32), smoothstep(0.30, 0.70, f));
                    albedo = mix(albedo, vec3(0.46, 0.33, 0.20), smoothstep(0.55, 0.85, g) * 0.5);
                    float cloud = smoothstep(0.60, 0.78, fbm(q * 1.7 + 23.0)) * 0.35;
                    albedo = mix(albedo, vec3(0.95), cloud);
                }
                float facing = clamp(dot(n, -d), 0.0, 1.0);
                float lit = mix(0.55, 1.0, smoothstep(-0.2, 0.7, dot(n, sunwards)));
                vec3 surface = albedo * lit;
                float haze = pow(1.0 - facing, 4.5);
                float bright = 0.35 + 0.65 * smoothstep(-0.4, 0.8, dot(normalize(n - planet_dir * dot(n, planet_dir)), normalize(rim_light - planet_dir * dot(rim_light, planet_dir))));
                surface = mix(surface, atmosphere * 1.6, clamp(haze * bright * 0.9, 0.0, 0.9));
                col = surface;
            } else {
                // The atmosphere's glow outside the limb, brightest toward the sun's side.
                float over = ang - R;
                float glow = exp(-over / 0.028);
                vec3 away = normalize(d - planet_dir * cosA + vec3(0.0001));
                float bright = 0.30 + 0.70 * smoothstep(-0.4, 0.8, dot(away, normalize(rim_light - planet_dir * dot(rim_light, planet_dir))));
                col = col * (1.0 - clamp(glow, 0.0, 1.0)) + atmosphere * glow * bright * 1.2;
            }
            COLOR = col;
            }
        }
        """;
}
