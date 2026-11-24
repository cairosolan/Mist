using System.IO;
using System.Windows.Forms;

namespace Mist;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        int dumpAt = -1, themeIdx = -1;
        float zoom = 0f;
        var files = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--register": Association.RegisterWithUi(); return;
                case "--unregister": Association.Unregister(out var rep); MessageBox.Show(rep, "Mist"); return;
                case "--makeicons":
                    if (i + 1 < args.Length) { IconDesign.Emit(args[i + 1]); i++; }
                    return;
                case "--dump": dumpAt = i; break;
                case "--opaque": DebugFlags.Opaque = true; break;
                case "--nodwm": DebugFlags.NoDwm = true; break;
                case "--gdi": DebugFlags.UseGdi = true; break;
                case "--copy": DebugFlags.SourceCopy = true; break;
                case "--lowglass":
                    if (i + 1 < args.Length && byte.TryParse(args[i + 1], out byte a)) { DebugFlags.LowGlassAlpha = a; i++; }
                    break;
                case "--theme":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int t)) { themeIdx = t; i++; }
                    break;
                case "--zoom":
                    if (i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float z)) { zoom = z; i++; }
                    break;
                default:
                    if (!args[i].StartsWith("--")) files.Add(args[i]);
                    break;
            }
        }

        var form = new MainForm();
        if (themeIdx >= 0) form.SetThemePublic(themeIdx);
        if (zoom > 0) form.SetZoomPublic(zoom);

        if (dumpAt >= 0 && files.Count > 0)
        {
            if (files.Count > 1 && File.Exists(files[1])) form.LoadDocument(files[1]);
            form.DumpPreview(files[0], 1000, 680);
            return;
        }

        if (files.Count > 0)
        {
            var path = Path.GetFullPath(files[0]);
            if (File.Exists(path)) form.Shown += (_, _) => form.LoadDocument(path);
        }

        Application.Run(form);
    }
}
