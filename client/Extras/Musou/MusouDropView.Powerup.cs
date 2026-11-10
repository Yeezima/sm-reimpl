using Godot;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// A powerup drop in the world (owner, 2026-11-10: "change the drop cards UI elements to be much more minimalistic and match the black and white theme"): a small black tile facing the camera with the
/// powerup's white icon (<see cref="MusouPowerupIcons"/>) and a thin white frame, its name above in small white capitals, a thin white ring on the floor and a faint thin white line up from it so it can
/// be found across a fight; it bobs but does not turn, carries no light and no gold. Sizes ours.
/// </summary>
public sealed partial class MusouDropView
{
    /// <summary>A powerup's look: no light, no turn.</summary>
    public bool Minimal { get; private set; }
    public MusouPowerup Powerup { get; private set; }

    const float TileSize = 0.62f, TileLift = 1.15f, RingRadius = 0.75f, LineHeight = 7f;

    static StandardMaterial3D? _tileBack, _ringWhite, _lineWhite;

    public static MusouDropView CreatePowerup(ushort id, ushort item, MusouPowerup kind)
    {
        var view = new MusouDropView { Id = id, Item = item, Title = MusouPowerupIcons.Name(kind), Name = $"MusouPowerup{id}", Minimal = true, Powerup = kind, Original = true };
        view._spin = new Node3D { Name = "Bob", Position = new Vector3(0f, 0f, TileLift) };
        view.AddChild(view._spin);
        _tileBack ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.02f, 0.02f, 0.02f, 0.82f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, CullMode = BaseMaterial3D.CullModeEnum.Disabled, DisableReceiveShadows = true,
        };
        // The tile: a dark square, a slightly larger white square behind it as its frame, the icon on top (all facing the camera).
        view._spin.AddChild(new Sprite3D
        {
            Texture = Frame(), PixelSize = (TileSize + 0.05f) / 64f, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Shaded = false, AlphaCut = SpriteBase3D.AlphaCutMode.Disabled,
            Modulate = new Color(1f, 1f, 1f, 0.95f), Layers = 2, RenderPriority = 0, NoDepthTest = false, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        });
        view._spin.AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(TileSize, TileSize), Material = _tileBack }, Layers = 2, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        view._spin.AddChild(new Sprite3D
        {
            Texture = MusouPowerupIcons.Of(kind), PixelSize = TileSize * 0.8f / 96f, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Shaded = false, RenderPriority = 2, Layers = 2,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        });
        view._spin.AddChild(new Label3D
        {
            Text = view.Title, FontSize = 40, OutlineSize = 8, PixelSize = 0.0022f, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Position = new Vector3(0f, 0f, TileSize * 0.5f + 0.18f),
            Modulate = new Color(0.92f, 0.92f, 0.92f), OutlineModulate = new Color(0f, 0f, 0f, 0.85f), Shaded = false, Layers = 2, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
        });
        // The floor ring and the faint line (seen across a fight; the line fades out close to the camera).
        _ringWhite ??= new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.95f, 0.95f, 0.95f), DisableReceiveShadows = true };
        view.AddChild(new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = RingRadius - 0.03f, OuterRadius = RingRadius + 0.03f, Rings = 48, RingSegments = 4, Material = _ringWhite },
            Basis = new Basis(Vector3.Right, Mathf.Pi / 2f).Scaled(new Vector3(1f, 1f, 0.2f)), Position = new Vector3(0f, 0f, 0.05f), Layers = 2, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        _lineWhite ??= new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 1f, 1f, 0.35f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.PixelAlpha, DistanceFadeMinDistance = 3f, DistanceFadeMaxDistance = 12f, DisableReceiveShadows = true,
        };
        view.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.02f, BottomRadius = 0.02f, Height = LineHeight, CapTop = false, CapBottom = false, RadialSegments = 6, Material = _lineWhite },
            Rotation = new Vector3(Mathf.Pi / 2f, 0f, 0f), Position = new Vector3(0f, 0f, TileLift + TileSize * 0.5f + 0.45f + LineHeight / 2f), Layers = 2,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return view;
    }

    static ImageTexture? _frame;

    /// <summary>A white square outline (the tile's frame), 64 x 64.</summary>
    static ImageTexture Frame()
    {
        if (_frame is not null) return _frame;
        var img = Image.CreateEmpty(64, 64, false, Image.Format.La8);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int edge = System.Math.Min(System.Math.Min(x, y), System.Math.Min(63 - x, 63 - y));
                img.SetPixel(x, y, new Color(1f, 1f, 1f, edge < 2 ? 1f : 0f));
            }
        img.GenerateMipmaps();
        return _frame = ImageTexture.CreateFromImage(img);
    }
}
