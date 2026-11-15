using System;
using System.Collections.Generic;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The main menu's numbers in the pixels of the concept's panel 01 screen (phase 5, owner 2026-11-15, source <c>owner-ref-panel01-v2.webp</c>, 1006 x 598: the whole panel at about 1.3x the old four-panel's). "Screen px" are the new image's
/// pixels with the origin at the screen's top-left corner (18, 38); the screen is 975 x 548 (16:9). The overlay is the screen's left <see cref="OverlayW"/> columns, built at 4x and drawn on a window at scale height / <see cref="ScreenH"/>.
/// The outline is the owner's orange line, registered onto this image (SIFT features of his marked picture against this one, similarity fit, 1325 inliers). Positions of the plates and labels were measured on this image
/// (row and column profiles, thresholded text); the regions of the older phase 3 build that the same artwork shares (wordmark window, crest, seat, nameplate) started from that build's crop numbers carried over by the
/// registered similarity (<see cref="X"/>, <see cref="Y"/>, <see cref="Z"/>: 0.8913 scale, the two images' offset) and were checked on the overlay, the compare's heat map and zoomed crops.
/// </summary>
public static class Panel01
{
    public const int ScreenW = 975, ScreenH = 548, OverlayW = 487, Up = 4;
    public static readonly Vector2I Origin = new(18, 38);

    /// <summary>The old crop's coordinates in screen px (the registered similarity): position and size.</summary>
    public static float X(float u) => u * 0.8913f + 24.08f;
    public static float Y(float v) => v * 0.8913f + 3.37f;
    public static float Z(float len) => len * 0.8913f;

    /// <summary>The button plates (screen px): x range, a dark plate's height, the pitch of the rows and the first row's top (where the live rows stand when there are five).</summary>
    public const float PlateX0 = 108f, PlateX1 = 407.5f, PlateH = 31.5f, PlatePitch = 38.2f, PlateTop1 = 263.4f;
    /// <summary>Margins of the button textures around the plate rectangle: sides, a dark plate's top and bottom, the gold plate's top and bottom.</summary>
    public const float TexSide = 3.6f, TexDarkV = 3.4f, TexGoldTop = 6.9f, TexGoldBottom = 3.6f;
    /// <summary>Where the live rows may stand (top, height) and the live wordmark and nameplate boxes.</summary>
    public const float RowsAreaTop = 258f, RowsAreaH = 193.4f;
    public static readonly Rect2 Wordmark = new(50.8f, 112.1f, 426f, 83.8f), Nameplate = new(129.3f, 487.3f, 236.2f, 26.7f);

    /// <summary>The plates' tops (the gold plate's includes its glow) and the painted labels' boxes, measured on the image (text pixels thresholded, padded 5 sides and 4 above and below).</summary>
    public static readonly float[] SrcTop = [256.5f, 301.5f, 339f, 377f, 414.8f];
    public static readonly Rect2[] SrcText = [new(192, 267, 138, 23), new(198, 305, 125, 22), new(213, 343, 99, 22), new(219, 381, 85, 22), new(237, 420, 47, 24)];
    public const float LabelCx = 261f, GoldLabelCy = 278.5f, DarkLabelDy = -0.4f;
    /// <summary>The painted labels' own centres and heights on their plates (fitted to the concept's own labels by cross-correlation in quarter pixels: the concept centres each a little differently, EXIT 1.5 px lower); labels without a painted original take the mean.</summary>
    public static float LabelCxOf(string id) => id switch { "armoury" => 263f, "options" => 262.25f, "exit" => 261f, _ => 261f };
    public static float LabelDyOf(string id) => id switch { "exit" => 1.35f, _ => -0.15f };

    /// <summary>The frame's outer outline (the notch between the shoulders is open at the top, the crest sits in it).</summary>
    public static readonly Vector2[] Frame = [new(62.4f, 239.6f), new(83.8f, 229.8f), new(104.3f, 231.5f), new(113.2f, 219.1f), new(157.8f, 219.1f), new(164.0f, 227.1f), new(343.2f, 227.1f), new(346.7f, 219.1f), new(385.1f, 219.1f), new(404.7f, 228.9f), new(418.9f, 239.6f), new(443.0f, 263.6f), new(459.0f, 279.7f), new(459.0f, 509.6f), new(450.1f, 516.8f), new(91.8f, 516.8f), new(48.1f, 509.6f), new(46.4f, 254.7f)];

    /// <summary>The owner's orange line (his marked picture registered onto this image, closed at the screen's borders and pushed outside them): everything inside is kept. Along the wall (top left) the line stood up to 13 px off the stone, over the concept's dark sky; those points hug the stone's edge instead.</summary>
    public static readonly Vector2[] Outline =
    [
        new(-30, -30), new(-30, 577), new(138, 577), new(138, 546), new(147, 542), new(146, 523), new(149, 520), new(229, 520), new(257, 522), new(259, 524),
        new(271, 524), new(277, 528), new(286, 528), new(298, 536), new(338, 536), new(342, 532), new(360, 532), new(362, 530), new(379, 530), new(380, 528),
        new(397, 528), new(403, 522), new(414, 522), new(427, 516), new(439, 516), new(440, 514), new(446, 513), new(449, 508), new(467, 506), new(472, 498),
        new(482, 494), new(485, 489), new(485, 417), new(484, 414), new(479, 412), new(479, 377), new(477, 375), new(475, 353), new(473, 347), new(467, 344),
        new(467, 338), new(463, 334), new(461, 323), new(459, 322), new(459, 255), new(453, 250), new(453, 244), new(450, 244), new(437, 236), new(432, 228),
        new(425, 227), new(420, 222), new(420, 214), new(414, 209), new(413, 205), new(391, 205), new(388, 203), new(389, 201), new(412, 201), new(413, 203),
        new(424, 204), new(428, 207), new(476, 208), new(479, 202), new(479, 163), new(477, 162), new(477, 143), new(475, 142), new(475, 136), new(455, 123),
        new(410, 123), new(408, 125), new(384, 127), new(379, 131), new(320, 130), new(316, 125), new(275, 123), new(271, 119), new(108, 119), new(105, 116),
        new(105, 112), new(104, 104), new(101, 98), new(100, 56), new(99, 48), new(96, 38), new(90, 28), new(82, 16), new(72, 9), new(64, 5), new(60, 1), new(60, -30)
    ];
}
