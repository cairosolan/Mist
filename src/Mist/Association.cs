using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Mist;

/// <summary>把当前 exe 注册为 .md 的默认打开程序（只写 HKCU，不需要管理员权限）。</summary>
internal static class Association
{
    private const string ProgId = "Mist.Markdown";
    private const string AppName = "Mist";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    private static string ExePath() => Process.GetCurrentProcess().MainModule!.FileName!;

    /// <summary>
    /// 把嵌入的 MistDoc.ico 释放到 %LOCALAPPDATA%\Mist\icons，返回路径。
    /// 走独立文件而不是 exe 里的图标索引：WinForms 只能把一张图标编进 exe，
    /// 程序标和文档标必须分开，注册表最多只能指到文件。
    /// </summary>
    private static string EnsureDocIcon()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mist", "icons");
        string path = Path.Combine(dir, "MistDoc.ico");
        try
        {
            Directory.CreateDirectory(dir);
            using var s = typeof(Association).Assembly.GetManifestResourceStream("MistDoc.ico");
            if (s is null) return path;
            bool stale = !File.Exists(path) || new FileInfo(path).Length != s.Length;
            if (stale)
            {
                using var fs = File.Create(path);
                s.CopyTo(fs);
            }
        }
        catch { }
        return path;
    }

    public static bool Register(out string report)
    {
        var lines = new List<string>();
        string exe = ExePath();
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

            // 1. ProgId：类型名、图标、打开命令
            string docIcon = EnsureDocIcon();
            using (var prog = classes!.CreateSubKey(ProgId)!)
            {
                prog.SetValue(null, "Markdown 文档");
                prog.SetValue("FriendlyTypeName", "Markdown 文档");
                using (var icon = prog.CreateSubKey("DefaultIcon")!)
                    icon.SetValue(null, $"\"{docIcon}\",0");
                using (var cmd = prog.CreateSubKey(@"shell\open\command")!)
                    cmd.SetValue(null, $"\"{exe}\" \"%1\"");
            }

            // 2. 扩展名 → ProgId
            using (var ext = classes.CreateSubKey(".md")!)
            {
                ext.SetValue(null, ProgId);
                ext.SetValue("Content Type", "text/markdown");
                ext.SetValue("PerceivedType", "text");
                using (var owp = ext.CreateSubKey("OpenWithProgids")!)
                    owp.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            // 3. 让程序出现在"打开方式"列表里
            using (var apps = classes.CreateSubKey(@"Applications\Mist.exe")!)
            {
                apps.SetValue("FriendlyAppName", AppName);
                apps.SetValue("ApplicationCompany", AppName);
                using (var icon = apps.CreateSubKey("DefaultIcon")!)
                    icon.SetValue(null, $"\"{exe}\",0");
                using (var cmd = apps.CreateSubKey(@"shell\open\command")!)
                    cmd.SetValue(null, $"\"{exe}\" \"%1\"");
                using (var types = apps.CreateSubKey("SupportedTypes")!)
                    types.SetValue(".md", "");
            }
            lines.Add("写入 ProgId / 扩展名 / 打开方式");
            lines.Add("文档图标：" + docIcon);

            // 4. 让"默认应用"设置页能选到它（这是绕过 UserChoice 的正规入口）
            using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\Mist\Capabilities")!)
            {
                cap.SetValue("ApplicationName", AppName);
                cap.SetValue("ApplicationDescription", "极简 Markdown 阅读器");
                using (var fa = cap.CreateSubKey("FileAssociations")!)
                    fa.SetValue(".md", ProgId);
            }
            using (var ra = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")!)
                ra.SetValue(AppName, @"Software\Mist\Capabilities");
            lines.Add("注册到系统「默认应用」");

            // 5. 清掉旧的 UserChoice / UserChoiceLatest（Win10/11 的劫持键），失败也不影响上面的注册
            try
            {
                using var fe = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md", writable: true);
                foreach (var name in new[] { "UserChoice", "UserChoiceLatest" })
                {
                    if (fe?.GetSubKeyNames().Contains(name) != true) continue;
                    fe.DeleteSubKeyTree(name);
                    lines.Add($"已清除旧的 {name}");
                }
            }
            catch (Exception ex) { lines.Add($"UserChoice 未能清除（{ex.Message}），请在「默认应用」里手动选 Mist"); }

            SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0x0, IntPtr.Zero, IntPtr.Zero);
            report = string.Join("\n", lines);
            return true;
        }
        catch (Exception ex)
        {
            report = $"注册失败：{ex.Message}";
            return false;
        }
    }

    public static void Unregister(out string report)
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes", writable: true);
            classes?.DeleteSubKeyTree(ProgId, false);
            classes?.DeleteSubKeyTree(@"Applications\Mist.exe", false);
            using (var ext = classes?.OpenSubKey(".md", writable: true))
            {
                object? v = ext?.GetValue(null);
                if (v as string == ProgId) ext!.DeleteValue(null, false);
                ext?.DeleteSubKeyTree("OpenWithProgids", false);
            }
            using (var cap = Registry.CurrentUser.OpenSubKey(@"Software\Mist", writable: true))
                cap?.DeleteSubKeyTree("Capabilities", false);
            using (var ra = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
                if (ra is not null) ra.DeleteValue(AppName, false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            report = "已取消 .md 关联（原有默认程序可能需要重新设置）";
        }
        catch (Exception ex) { report = $"取消失败：{ex.Message}"; }
    }

    public static void RegisterWithUi() => MessageBox.Show(Register(out var r) ? $"已把 Mist 设为 .md 的默认打开程序。\n\n{r}\n\n若双击仍打开别的程序，请到「设置 → 应用 → 默认应用」里把 .md 指向 Mist。" : r,
        "Mist", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
