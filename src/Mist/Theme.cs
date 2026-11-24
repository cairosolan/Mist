using System.Drawing;

namespace Mist;

/// <summary>A theme is a set of semantic tokens. Colors carry straight alpha; the
/// compositor premultiplies them when it hands the frame to DWM.</summary>
internal sealed class Theme
{
    public string Name = "";
    public bool Dark;

    /// <summary>Base glass layer: alpha here is what makes the backdrop readable.</summary>
    public Color Glass;
    /// <summary>Optional accent wash painted on top of the glass (experimental themes).</summary>
    public Color Tint;
    public Color Text;
    public Color Muted;
    public Color Rule;
    public Color CodeBg;
    public Color CodeBorder;
    /// <summary>Code text is drawn lighter/heavier than body text so it stays readable on the tinted box.</summary>
    public Color CodeText;
    public Color Accent;
    public Color Hover;
    /// <summary>Opaque sample used for the theme swatch in the title bar.</summary>
    public Color Swatch;

    public static readonly Theme PureLight = new()
    {
        Name = "纯白", Dark = false,
        Glass = Color.FromArgb(242, 255, 255, 255),
        Tint = Color.FromArgb(0, 255, 255, 255),
        Text = Color.FromArgb(255, 17, 17, 17),
        Muted = Color.FromArgb(255, 118, 118, 118),
        Rule = Color.FromArgb(190, 0, 0, 0),
        CodeBg = Color.FromArgb(232, 245, 245, 245),
        CodeBorder = Color.FromArgb(150, 0, 0, 0),
        CodeText = Color.FromArgb(255, 10, 10, 10),
        Accent = Color.FromArgb(255, 43, 92, 200),
        Hover = Color.FromArgb(26, 0, 0, 0),
        Swatch = Color.FromArgb(255, 250, 250, 250),
    };

    public static readonly Theme PureDark = new()
    {
        Name = "纯黑", Dark = true,
        Glass = Color.FromArgb(230, 9, 9, 9),
        Tint = Color.FromArgb(0, 255, 255, 255),
        Text = Color.FromArgb(255, 236, 236, 236),
        Muted = Color.FromArgb(255, 138, 138, 138),
        Rule = Color.FromArgb(170, 255, 255, 255),
        CodeBg = Color.FromArgb(228, 32, 32, 32),
        CodeBorder = Color.FromArgb(110, 255, 255, 255),
        CodeText = Color.FromArgb(255, 248, 248, 248),
        Accent = Color.FromArgb(255, 138, 180, 248),
        Hover = Color.FromArgb(30, 255, 255, 255),
        Swatch = Color.FromArgb(255, 12, 12, 12),
    };

    public static readonly Theme Ivory = new()
    {
        Name = "米白", Dark = false,
        Glass = Color.FromArgb(240, 255, 252, 245),
        Tint = Color.FromArgb(18, 196, 142, 60),
        Text = Color.FromArgb(255, 34, 29, 23),
        Muted = Color.FromArgb(255, 124, 113, 96),
        Rule = Color.FromArgb(190, 90, 70, 40),
        CodeBg = Color.FromArgb(236, 247, 241, 228),
        CodeBorder = Color.FromArgb(120, 120, 90, 50),
        CodeText = Color.FromArgb(255, 28, 24, 18),
        Accent = Color.FromArgb(255, 168, 106, 32),
        Hover = Color.FromArgb(24, 90, 60, 20),
        Swatch = Color.FromArgb(255, 250, 243, 229),
    };

    public static readonly Theme Ink = new()
    {
        Name = "墨蓝", Dark = true,
        Glass = Color.FromArgb(232, 12, 18, 34),
        Tint = Color.FromArgb(22, 60, 130, 200),
        Text = Color.FromArgb(255, 228, 233, 242),
        Muted = Color.FromArgb(255, 124, 137, 162),
        Rule = Color.FromArgb(160, 120, 160, 220),
        CodeBg = Color.FromArgb(230, 24, 34, 58),
        CodeBorder = Color.FromArgb(120, 110, 160, 220),
        CodeText = Color.FromArgb(255, 240, 245, 252),
        Accent = Color.FromArgb(255, 111, 192, 224),
        Hover = Color.FromArgb(30, 120, 170, 230),
        Swatch = Color.FromArgb(255, 14, 21, 38),
    };

    public static Theme[] All => new[] { PureLight, PureDark, Ivory, Ink };
}
