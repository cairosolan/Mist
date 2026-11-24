namespace Mist;

internal static class DebugFlags
{
    /// <summary>Force a fully opaque frame (isolates alpha/backdrop issues).</summary>
    public static bool Opaque;

    /// <summary>Skip all DWM backdrop / frame-extension calls.</summary>
    public static bool NoDwm;

    /// <summary>Present the frame with GDI+ DrawImage instead of AlphaBlend.</summary>
    public static bool UseGdi;

    /// <summary>Use CompositingMode.SourceCopy so per-pixel alpha survives to DWM.</summary>
    public static bool SourceCopy;

    /// <summary>Diagnostic: force a much lower glass alpha so backdrop bleed is obvious.</summary>
    public static byte LowGlassAlpha;
}
