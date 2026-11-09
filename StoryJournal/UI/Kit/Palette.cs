using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace StoryJournal.UI;

/// <summary>
/// Story Journal's colours, "Ink &amp; Ribbon" as the site has them (its --sj-* tokens): warm ink grounds
/// instead of cool grey, paper-coloured words, the cherry red (#f2425f) as the ribbon (where you are, what's
/// yours to press, a seal) on its deeper tile red (#c21d3a) for the one filled button, and candle gold only
/// for your own words. The older names below are kept, mapped onto the new tokens, so every part drawn with
/// them takes the warm ink too.
/// </summary>
public static class Palette
{
    // ── The tokens (the site's --sj-*) ─────────────────────────────────────────────────────────────
    public static readonly Vector4 InkGround = Hex(0x0c0a09);              // --sj-ground: the window's foot
    public static readonly Vector4 Ground2 = Hex(0x110e0d);                // --sj-ground-2: wells, fields
    public static readonly Vector4 Leaf = Hex(0x171412);                   // --sj-leaf: windows (at 97%), cards
    public static readonly Vector4 LeafRaised = Hex(0x1f1b18);             // --sj-raised: a card in a card, a menu
    public static readonly Vector4 Hover = Hex(0x2a2420);                  // --sj-hover: hover, a pressed segment, a track
    public static readonly Vector4 InkLine = Hex(0x2b2421);                 // --sj-line
    public static readonly Vector4 InkLineStrong = Hex(0x453a34);          // --sj-line-strong

    public static readonly Vector4 Paper = Hex(0xf4ece2);                  // --sj-ink: headings, what matters
    public static readonly Vector4 Body = Hex(0xd8cdc1);                   // --sj-body: reading
    public static readonly Vector4 Mute = Hex(0xab9d90);                   // --sj-muted: second lines
    public static readonly Vector4 Fade = Hex(0x8f8276);                   // --sj-faint: the least

    public static readonly Vector4 Ribbon = Hex(0xf2425f);                 // --sj-ribbon
    public static readonly Vector4 RibbonDeep = Hex(0xc21d3a);             // --sj-ribbon-deep: the filled button (white on it 5.9:1)
    public static readonly Vector4 RibbonDark = Hex(0x8e1229);             // --sj-ribbon-dark
    public static readonly Vector4 RibbonPress = Hex(0xd4223f);            // the filled button pointed at
    public static readonly Vector4 RibbonSoft = Rgba(242, 66, 95, 0.12f);  // --sj-ribbon-soft
    public static readonly Vector4 RibbonLine = Rgba(242, 66, 95, 0.42f);  // --sj-ribbon-line

    public static readonly Vector4 Quill = Hex(0xe9c47f);                  // --sj-quill: your own words
    public static readonly Vector4 QuillInk = Hex(0xf3e3c2);               // --sj-quill-ink
    public static readonly Vector4 QuillSoft = Rgba(233, 196, 127, 0.09f); // --sj-quill-soft
    public static readonly Vector4 QuillLine = Rgba(233, 196, 127, 0.32f); // --sj-quill-line

    public static readonly Vector4 Good = Hex(0x86d9a8);                   // --sj-good
    public static readonly Vector4 WarnInk = Hex(0xf0b45c);                // --sj-warn
    public static readonly Vector4 Sky = Hex(0x9fb8e8);                    // --sj-sky

    /// <summary>The wax of a seal, light to dark.</summary>
    public static readonly Vector4 WaxLight = Hex(0xd9324f);
    public static readonly Vector4 WaxMid = Hex(0xa41733);
    public static readonly Vector4 WaxDark = Hex(0x7c0f24);

    // ── The older names, onto the tokens ───────────────────────────────────────────────────────────
    public static readonly Vector4 Ground = Leaf;                          // the window
    public static readonly Vector4 Footer = InkGround;                     // the title bar, the window's foot
    public static readonly Vector4 Card = Leaf;                            // boxes
    public static readonly Vector4 Raised = LeafRaised;                    // a box inside a box
    public static readonly Vector4 Float = LeafRaised;                     // menus, tooltips, toasts
    public static readonly Vector4 Field = Ground2;                        // fields
    public static readonly Vector4 Tile = LeafRaised;                      // icon wells, the quiet tag
    public static readonly Vector4 ButtonFill = Rgba(0, 0, 0, 0);          // an outlined button's inside
    public static readonly Vector4 Picked = Hover;                         // the picked one in a pick-one pill

    public static readonly Vector4 White = Hex(0xffffff);
    public static readonly Vector4 Ink = Paper;
    public static readonly Vector4 Soft = Body;                            // words in boxes
    public static readonly Vector4 Light = Body;                           // quiet buttons, links
    public static readonly Vector4 Muted = Mute;                           // labels
    public static readonly Vector4 Dim = Fade;                             // small mono labels, times
    public static readonly Vector4 Faint = Fade;                           // what hardly needs reading

    public static readonly Vector4 Accent = Ribbon;
    public static readonly Vector4 AccentHover = Hex(0xf56c82);
    public static readonly Vector4 AccentSoft = Hex(0xf78e9f);             // cherry words on the dark
    public static readonly Vector4 AccentText = Ribbon;                    // cherry notes and links
    public static readonly Vector4 AccentDeep = RibbonDeep;                // the mark's tile; the filled button
    public static readonly Vector4 AccentDim = Hex(0x7a2131);
    public static readonly Vector4 OnAccent = White;                       // words on the filled button
    public static readonly Vector4 AccentWash = Over(RibbonSoft, Leaf);    // a ribboned card's fill
    public static readonly Vector4 AccentTile = Over(Rgba(242, 66, 95, 0.16f), Leaf);
    public static readonly Vector4 AccentPicked = Rgba(242, 66, 95, 0.16f);
    public static readonly Vector4 AccentRow = Rgba(242, 66, 95, 0.07f);   // the row under way
    public static readonly Vector4 AccentEdge = RibbonLine;                // a ribboned card's edge

    public static readonly Vector4 Gain = Good;
    public static readonly Vector4 Loss = Hex(0xff8a8a);

    public static readonly Vector4 Danger = Hex(0xff8a8a);
    public static readonly Vector4 DangerSolid = Hex(0xff4f4f);
    public static readonly Vector4 DangerWash = Rgba(255, 79, 79, 0.1f);
    public static readonly Vector4 DangerEdge = Rgba(255, 79, 79, 0.45f);
    public static readonly Vector4 Success = Good;
    public static readonly Vector4 SuccessWash = Rgba(134, 217, 168, 0.1f);
    public static readonly Vector4 SuccessEdge = Rgba(134, 217, 168, 0.4f);
    public static readonly Vector4 Warn = WarnInk;

    public static readonly Vector4 LineFaint = Rgba(255, 240, 225, 0.06f); // dividers in boxes, hover fills
    public static readonly Vector4 LinePicked = Rgba(255, 240, 225, 0.09f);
    public static readonly Vector4 Line = InkLine;
    public static readonly Vector4 LineStrong = InkLineStrong;
    public static readonly Vector4 LineHover = Hex(0x5c4e46);
    public static readonly Vector4 LineDashed = InkLineStrong;
    public static readonly Vector4 RowHover = Rgba(255, 240, 225, 0.04f);
    public static readonly Vector4 RowStripe = Rgba(255, 240, 225, 0.025f);
    public static readonly Vector4 Track = Hover;                          // a progress bar's track

    // Radii (CSS px, before Dalamud's global scale).
    public const float RadiusSm = 8;
    public const float RadiusMd = 12;
    public const float RadiusLg = 14;    // cards (the site's --sj-r)
    public const float RadiusXl = 20;    // leaves (--sj-r-lg)
    public const float Radius2Xl = 20;
    public const float RadiusCard = 14;
    public const float RadiusPill = 999;

    /// <summary>Everything drawn is multiplied by this (0.5 for a disabled button, an animation's fade).</summary>
    public static float Opacity { get; set; } = 1f;

    public static Vector4 Hex(uint rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    public static Vector4 Rgba(int r, int g, int b, float a) => new(r / 255f, g / 255f, b / 255f, a);

    /// <summary>A translucent colour flattened over an opaque one.</summary>
    public static Vector4 Over(Vector4 top, Vector4 under) =>
        new(under.X + (top.X - under.X) * top.W, under.Y + (top.Y - under.Y) * top.W, under.Z + (top.Z - under.Z) * top.W, 1f);

    public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    public static Vector4 Mix(Vector4 a, Vector4 b, float t) => a + (b - a) * t;

    public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, c.W * Opacity));
}
