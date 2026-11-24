using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Mist;

public sealed class MainForm : Form
{
    private readonly FontCache _fonts = new();
    private readonly Bitmap _measureBmp = new(8, 8, PixelFormat.Format32bppArgb);
    private readonly Graphics _measureG;

    private Theme _theme = Theme.PureLight;
    private int _themeIndex = 0;

    private List<Block> _blocks = new();
    private DocLayout? _layout;
    private string _filePath = "";
    private string _rawText = "";
    private string? _notice;
    private bool _copied;
    private readonly System.Windows.Forms.Timer _copyTimer = new() { Interval = 1400 };

    private float _scale = 1f;
    private float _zoom = 1f;
    private float _scroll;

    // 文本选择：锚点和焦点都是 (绘制指令下标, 字符下标)
    private int _selAOp = -1, _selACh, _selBOp = -1, _selBCh;
    private bool _selecting;

    private Bitmap? _buf;
    private Graphics? _bufG;

    private Icon? _appIcon;
    private Bitmap? _appIconBmp;

    private RectangleF _rOpen, _rCopy, _rMin, _rMax, _rClose;
    private RectangleF[] _rSwatch = Array.Empty<RectangleF>();
    private int _hover = -1;

    private float BarH => Sc(46);

    private float Sc(float v) => v * _scale;

    public MainForm()
    {
        _measureG = Graphics.FromImage(_measureBmp);
        _measureG.TextRenderingHint = TextRenderingHint.AntiAlias;
        _measureG.SmoothingMode = SmoothingMode.AntiAlias;

        Text = "Mist";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1080, 760);
        MinimumSize = new Size(520, 380);
        BackColor = Color.Black;
        AllowDrop = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = false;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        _scale = DeviceDpi / 96f;

        _copyTimer.Tick += (_, _) => { _copyTimer.Stop(); _copied = false; Invalidate(); };

        LoadIcon();
    }

    /// <summary>窗口图标取自嵌入的 Mist.ico（多尺寸），标题栏左侧也画同一个标。</summary>
    private void LoadIcon()
    {
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("Mist.ico");
            if (s is null) return;
            _appIcon = new Icon(s, new Size(64, 64));
            _appIconBmp = _appIcon.ToBitmap();
            Icon = _appIcon;
        }
        catch { }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= Win32.WS_THICKFRAME;
            cp.Style &= ~0x00C00000; // no WS_CAPTION
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDwm();
    }

    private void ApplyDwm()
    {
        var h = Handle;
        Win32.SetInt(h, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, _theme.Dark ? 1 : 0);
        Win32.SetInt(h, 19, _theme.Dark ? 1 : 0); // Win10 legacy attribute
        Win32.SetInt(h, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, Win32.DWMWCP_ROUND);

        if (DebugFlags.NoDwm) return;

        int backdrop = Win32.DWMSBT_TRANSIENTWINDOW; // acrylic: real blur of what's behind
        int hr = Win32.DwmSetWindowAttribute(h, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        if (hr != 0)
        {
            backdrop = Win32.DWMSBT_MAINWINDOW;      // mica fallback
            Win32.DwmSetWindowAttribute(h, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        }

        var m = new Win32.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        Win32.DwmExtendFrameIntoClientArea(h, ref m);
    }

    // ---------- document ----------

    public void LoadDocument(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            string text = DecodeText(bytes);
            _blocks = MarkdownParser.Parse(text);
            _filePath = path;
            _rawText = text;
            _notice = null;
            Text = "Mist — " + Path.GetFileName(path);
            _scroll = 0;
            Log($"loaded {path} blocks={_blocks.Count}");
            Rebuild();
        }
        catch (Exception ex)
        {
            _notice = "无法打开文件：" + ex.Message;
            Invalidate();
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }

    private void Rebuild()
    {
        _layout = null;
        _selAOp = _selBOp = -1;
        _selACh = _selBCh = 0;
        Invalidate();
        Update();
    }

    private DocLayout GetLayout(int width, int height)
    {
        if (_layout is not null) return _layout;
        // 最大化/全屏时版心放宽，让正文左右延伸，避免大片留白
        float cap = WindowState == FormWindowState.Maximized ? Sc(1060) : Sc(660);
        float contentW = MathF.Min(cap, width - Sc(72));
        if (contentW < Sc(180)) contentW = MathF.Max(Sc(120), width - Sc(24));
        _layout = new Layouter(_theme, _fonts, _measureG)
            .Build(_blocks, contentW, _scale, _zoom, Sc(34), Sc(80));
        return _layout;
    }

    private float ContentLeft(int width) => (width - GetLayout(width, ClientSize.Height).ContentWidth) / 2f;

    private float MaxScroll(int height)
    {
        var l = GetLayout(ClientSize.Width, height);
        float vp = height - BarH;
        return MathF.Max(0, l.Height - vp + Sc(24));
    }

    // ---------- painting ----------

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Win32.WM_NCCALCSIZE:
                if (m.WParam != IntPtr.Zero) { m.Result = IntPtr.Zero; return; }
                break;

            case Win32.WM_NCHITTEST:
            {
                base.WndProc(ref m);
                if (m.Result == (IntPtr)Win32.HTCLIENT)
                {
                    var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                    m.Result = (IntPtr)HitTestChrome(p);
                }
                return;
            }

            case Win32.WM_NCPAINT:
                m.Result = IntPtr.Zero;
                return;

            case Win32.WM_ERASEBKGND:
                m.Result = (IntPtr)1;
                return;

            case Win32.WM_PAINT:
            {
                var ps = default(Win32.PAINTSTRUCT);
                IntPtr dc = Win32.BeginPaint(m.HWnd, out ps);
                if (dc != IntPtr.Zero)
                {
                    Compose(dc, ClientSize.Width, ClientSize.Height);
                    Win32.EndPaint(m.HWnd, ref ps);
                }
                m.Result = IntPtr.Zero;
                return;
            }

            case Win32.WM_NCACTIVATE:
                base.WndProc(ref m);
                Invalidate();
                return;

            case Win32.WM_DPICHANGED:
                base.WndProc(ref m);
                _scale = DeviceDpi / 96f;
                DisposeBuffer();
                Rebuild();
                return;
        }
        base.WndProc(ref m);
    }

    private int HitTestChrome(Point p)
    {
        if (WindowState == FormWindowState.Maximized)
            return Win32.HTCLIENT;

        float edge = Sc(6);
        int w = ClientSize.Width, h = ClientSize.Height;
        bool left = p.X < edge, right = p.X >= w - edge, top = p.Y < edge, bottom = p.Y >= h - edge;
        if (top && left) return Win32.HTTOPLEFT;
        if (top && right) return Win32.HTTOPRIGHT;
        if (bottom && left) return Win32.HTBOTTOMLEFT;
        if (bottom && right) return Win32.HTBOTTOMRIGHT;
        if (left) return Win32.HTLEFT;
        if (right) return Win32.HTRIGHT;
        if (top) return Win32.HTTOP;
        if (bottom) return Win32.HTBOTTOM;

        if (p.Y < BarH && HitButton(p) < 0)
            return Win32.HTCAPTION;

        return Win32.HTCLIENT;
    }

    private int HitButton(Point p)
    {
        if (_rClose.Contains(p)) return 3;
        if (_rMax.Contains(p)) return 2;
        if (_rMin.Contains(p)) return 1;
        if (_rOpen.Contains(p)) return 0;
        if (_rCopy.Contains(p)) return 5;
        for (int i = 0; i < _rSwatch.Length; i++)
            if (_rSwatch[i].Contains(p)) return 10 + i;
        return -1;
    }

    private void EnsureBuffer(int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        if (_buf is not null && _buf.Width == w && _buf.Height == h) return;
        DisposeBuffer();
        _buf = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        _bufG = Graphics.FromImage(_buf);
        _bufG.SmoothingMode = SmoothingMode.AntiAlias;
        _bufG.TextRenderingHint = TextRenderingHint.AntiAlias;
        _bufG.PixelOffsetMode = PixelOffsetMode.HighQuality;
        _bufG.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }

    private void DisposeBuffer()
    {
        _bufG?.Dispose();
        _bufG = null;
        _buf?.Dispose();
        _buf = null;
    }

    /// <summary>Paint the frame onto the window surface with per-pixel alpha.
    /// SourceCopy hands the alpha channel straight to DWM, which is what lets the
    /// system backdrop (Acrylic / Mica) show through the rounded glass sheet.</summary>
    private void Compose(IntPtr hdc, int w, int h)
    {
        try
        {
            EnsureBuffer(w, h);
            if (_buf is null || _bufG is null) return;

            RenderFrame(_bufG, w, h);

            using var g = Graphics.FromHdc(hdc);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_buf, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel);
        }
        catch (Exception ex)
        {
            Log("Compose: " + ex);
        }
    }

    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "mist-error.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    /// <summary>Render one frame to PNG (used by --dump for offline checks).</summary>
    public void DumpPreview(string outPath, int w, int h)
    {
        _scale = 1f;
        _layout = null;
        EnsureBuffer(w, h);
        RenderFrame(_bufG!, w, h);

        using var target = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var tg = Graphics.FromImage(target))
        using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w, h),
            Color.FromArgb(255, 208, 216, 226), Color.FromArgb(255, 240, 242, 245), 60f))
        {
            tg.FillRectangle(lg, 0, 0, w, h);
            tg.DrawImage(_buf!, 0, 0, w, h);
        }
        target.Save(outPath, ImageFormat.Png);
    }

    private void RenderFrame(Graphics g, int w, int h)
    {
        g.Clear(Color.Transparent);

        int radius = WindowState == FormWindowState.Maximized ? 0 : (int)Sc(9);
        var glass = _theme.Glass;
        if (DebugFlags.Opaque) glass = Color.FromArgb(255, glass);
        if (DebugFlags.LowGlassAlpha > 0) glass = Color.FromArgb(DebugFlags.LowGlassAlpha, glass);
        using (var path = RoundRect(new RectangleF(0, 0, w, h), radius))
        using (var br = new SolidBrush(glass))
            g.FillPath(br, path);

        if (_theme.Tint.A > 0)
        {
            using var lg = new LinearGradientBrush(
                new RectangleF(0, 0, w, h),
                _theme.Tint,
                Color.FromArgb(0, _theme.Tint),
                90f);
            using var path = RoundRect(new RectangleF(0, 0, w, h), radius);
            g.FillPath(lg, path);
        }

        DrawChrome(g, w, h);

        // document viewport
        float barB = BarH;
        var clip = new RectangleF(0, barB, w, h - barB);
        var old = g.Clip;
        g.SetClip(clip, CombineMode.Intersect);
        DrawDocument(g, w, h);
        g.Clip = old;

        DrawScrollbar(g, w, h);
    }

    private void DrawChrome(Graphics g, int w, int h)
    {
        // hairline under the bar
        using (var br = new SolidBrush(Color.FromArgb(Math.Min(120, (int)_theme.Rule.A), _theme.Rule)))
            g.FillRectangle(br, 0, BarH - 1, w, 1);

        // window buttons, laid out from the right edge
        float bw = Sc(42), bh = BarH;
        _rClose = new RectangleF(w - bw, 0, bw, bh);
        _rMax = new RectangleF(w - bw * 2, 0, bw, bh);
        _rMin = new RectangleF(w - bw * 3, 0, bw, bh);

        // theme swatches, right of the open button
        var sw = Sc(10.5f);
        float gap = Sc(10);
        int n = Theme.All.Length;
        float rowW = n * sw + (n - 1) * gap;
        float rowX = _rMin.Left - Sc(22) - rowW;
        _rSwatch = new RectangleF[n];
        for (int i = 0; i < n; i++)
            _rSwatch[i] = new RectangleF(rowX + i * (sw + gap), BarH / 2 - sw / 2, sw, sw);

        // open button
        float openW = Sc(42);
        _rOpen = new RectangleF(rowX - Sc(12) - openW, Sc(9), openW, BarH - Sc(18));

        // copy button（在"打开"左侧）
        float copyW = Sc(50);
        _rCopy = new RectangleF(_rOpen.Left - Sc(6) - copyW, Sc(9), copyW, BarH - Sc(18));

        DrawHoverRect(g, _rClose, 3);
        DrawHoverRect(g, _rMax, 2);
        DrawHoverRect(g, _rMin, 1);

        // glyphs
        var gc = _theme.Muted;
        using (var pen = new Pen(gc, MathF.Max(1f, Sc(1)))
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round
        })
        {
            float cx = _rMin.Left + _rMin.Width / 2, cy = _rMin.Top + _rMin.Height / 2;
            g.DrawLine(pen, cx - Sc(5), cy, cx + Sc(5), cy);

            cx = _rMax.Left + _rMax.Width / 2;
            var mr = new RectangleF(cx - Sc(5), cy - Sc(5), Sc(10), Sc(10));
            g.DrawRectangle(pen, mr.X, mr.Y, mr.Width, mr.Height);

            cx = _rClose.Left + _rClose.Width / 2;
            g.DrawLine(pen, cx - Sc(5), cy - Sc(5), cx + Sc(5), cy + Sc(5));
            g.DrawLine(pen, cx - Sc(5), cy + Sc(5), cx + Sc(5), cy - Sc(5));
        }

        // swatches
        for (int i = 0; i < _rSwatch.Length; i++)
        {
            var th = Theme.All[i];
            var r = _rSwatch[i];
            using var path = RoundRect(r, r.Width / 2);
            using var fill = new SolidBrush(Color.FromArgb(245, th.Swatch));
            g.FillPath(fill, path);
            using var pen = new Pen(Color.FromArgb(i == _themeIndex ? 200 : 70, _theme.Dark ? Color.White : Color.Black),
                MathF.Max(1f, Sc(i == _themeIndex ? 1.6f : 1f)));
            g.DrawPath(pen, path);
        }

        // open button
        if (_hover == 0)
        {
            using var path = RoundRect(_rOpen, Sc(6));
            using var br = new SolidBrush(_theme.Hover);
            g.FillPath(br, path);
        }
        var of = _fonts.Get("Microsoft YaHei UI", Sc(12.2f), FontStyle.Regular);
        using (var br = new SolidBrush(_hover == 0 ? _theme.Text : _theme.Muted))
            g.DrawString("打开", of, br, _rOpen.Left + Sc(10), _rOpen.Top + (_rOpen.Height - of.GetHeight(_measureG)) / 2 + Sc(1), Layouter.Typographic);

        // copy button
        if (_hover == 5 || _copied)
        {
            using var cpath = RoundRect(_rCopy, Sc(6));
            using var cbr = new SolidBrush(_copied ? Color.FromArgb(38, _theme.Accent) : _theme.Hover);
            g.FillPath(cbr, cpath);
        }
        string copyLabel = _copied ? "已复制" : "复制";
        float cw2 = g.MeasureString(copyLabel, of, PointF.Empty, Layouter.Typographic).Width;
        using (var br = new SolidBrush(_copied || _hover == 5 ? _theme.Text : _theme.Muted))
            g.DrawString(copyLabel, of, br, _rCopy.Left + (_rCopy.Width - cw2) / 2,
                _rCopy.Top + (_rCopy.Height - of.GetHeight(_measureG)) / 2 + Sc(1), Layouter.Typographic);

        // 标题栏左侧：程序标 + 文件名
        float markSize = Sc(19);
        float markY = (BarH - markSize) / 2;
        if (_appIconBmp is not null)
        {
            var old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_appIconBmp, new RectangleF(Sc(15), markY, markSize, markSize));
            g.InterpolationMode = old;
        }

        // file name
        string name = _filePath.Length > 0 ? Path.GetFileName(_filePath) : "Mist";
        var tf = _fonts.Get("Microsoft YaHei UI", Sc(12.6f), FontStyle.Regular);
        float tx = Sc(15) + markSize + Sc(9), ty = (BarH - tf.GetHeight(_measureG)) / 2 + Sc(1);
        var nameWidth = _rOpen.Left - Sc(16) - tx;
        using (var br = new SolidBrush(_theme.Muted))
            g.DrawString(name, tf, br, new RectangleF(tx, ty, MathF.Max(0, nameWidth), tf.GetHeight(_measureG) + Sc(4)), Layouter.Typographic);
    }

    private void DrawHoverRect(Graphics g, RectangleF r, int id)
    {
        if (_hover != id) return;
        using var path = RoundRect(new RectangleF(r.Left + Sc(3), r.Top + Sc(4), r.Width - Sc(6), r.Height - Sc(8)), Sc(7));
        using var br = new SolidBrush(_theme.Hover);
        g.FillPath(br, path);
    }

    private void DrawDocument(Graphics g, int w, int h)
    {
        if (_layout is null) GetLayout(w, h);

        if (_blocks.Count == 0)
        {
            DrawEmpty(g, w, h);
            return;
        }

        var layout = _layout!;
        float dx = ContentLeft(w);
        float dy = BarH - _scroll;

        float top = BarH, bottom = h + Sc(8);
        DrawSelection(g, dx, dy);
        var brushes = new Dictionary<Color, SolidBrush>();

        foreach (var op in layout.Ops)
        {
            if (op.Bottom + dy < top) continue;
            if (op.Top + dy > bottom) break;

            switch (op.Kind)
            {
                case OpKind.Fill:
                {
                    if (!brushes.TryGetValue(op.Color, out var br))
                    {
                        br = new SolidBrush(op.Color);
                        brushes[op.Color] = br;
                    }
                    if (op.Radius > 0.5f)
                    {
                        using var path = RoundRect(new RectangleF(op.X + dx, op.Y + dy, op.W, op.H), op.Radius);
                        g.FillPath(br, path);
                    }
                    else g.FillRectangle(br, op.X + dx, op.Y + dy, op.W, op.H);
                    break;
                }
                case OpKind.Line:
                {
                    using var pen = new Pen(op.Color, MathF.Max(1f, Sc(1)));
                    using var path = RoundRect(new RectangleF(op.X + dx, op.Y + dy, op.W, op.H), op.Radius);
                    g.DrawPath(pen, path);
                    break;
                }
                case OpKind.Text:
                {
                    if (!brushes.TryGetValue(op.Color, out var br))
                    {
                        br = new SolidBrush(op.Color);
                        brushes[op.Color] = br;
                    }
                    g.DrawString(op.Text, op.Font, br, op.X + dx, op.Y + dy, Layouter.Typographic);
                    break;
                }
            }
        }

        foreach (var br in brushes.Values) br.Dispose();
    }

    private void DrawEmpty(Graphics g, int w, int h)
    {
        float cy = BarH + (h - BarH) * 0.36f;
        var t = _fonts.Get("Segoe UI", Sc(30f), FontStyle.Regular);
        var s = _fonts.Get("Microsoft YaHei UI", Sc(13.2f), FontStyle.Regular);

        using var b1 = new SolidBrush(_theme.Text);
        using var b2 = new SolidBrush(_theme.Muted);

        var sizeT = g.MeasureString("Mist", t, PointF.Empty, Layouter.Typographic);
        g.DrawString("Mist", t, b1, (w - sizeT.Width) / 2f, cy, Layouter.Typographic);

        if (_notice is not null)
        {
            var sz = g.MeasureString(_notice, s, PointF.Empty, Layouter.Typographic);
            g.DrawString(_notice, s, b2, (w - sz.Width) / 2f, cy + sizeT.Height + Sc(18), Layouter.Typographic);
            return;
        }

        string hint = "把 .md 文件拖进来，或按 Ctrl+O 打开";
        var hs = g.MeasureString(hint, s, PointF.Empty, Layouter.Typographic);
        g.DrawString(hint, s, b2, (w - hs.Width) / 2f, cy + sizeT.Height + Sc(16), Layouter.Typographic);
    }

    private void DrawScrollbar(Graphics g, int w, int h)
    {
        if (_layout is null) return;
        float vp = h - BarH;
        float total = _layout.Height;
        if (total <= vp + Sc(8)) return;

        float trackTop = BarH + Sc(8);
        float trackH = vp - Sc(16);
        float thumbH = MathF.Max(Sc(40), trackH * vp / total);
        float p = _scroll / MathF.Max(1f, total - vp + Sc(24));
        p = Math.Clamp(p, 0f, 1f);
        float thumbTop = trackTop + (trackH - thumbH) * p;

        var r = new RectangleF(w - Sc(9), thumbTop, Sc(4), thumbH);
        using var path = RoundRect(r, Sc(2));
        using var br = new SolidBrush(Color.FromArgb(_hover == 4 ? 140 : 70, _theme.Muted));
        g.FillPath(br, path);
    }

    internal static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0.5f || r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
        radius = MathF.Min(radius, MathF.Min(r.Width, r.Height) / 2f);
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- input ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragScroll)
        {
            float max = MaxScroll(ClientSize.Height);
            _scroll = Math.Clamp(_dragStart + (_dragFrom - e.Y) * 1.4f, 0, max);
            Invalidate();
            return;
        }

        // 拖选：鼠标移出文字范围时按同行最近的位置吸附，方便一路拖到行尾
        if (_selecting)
        {
            int op, ch;
            bool ok = HitTextAt(e.Location, out op, out ch) || NearestTextAt(e.Location, out op, out ch);
            if (ok && (op != _selBOp || ch != _selBCh))
            {
                _selBOp = op;
                _selBCh = ch;
                Invalidate();
            }
            Cursor = Cursors.IBeam;
            return;
        }

        int hit = HitButton(e.Location);
        if (_scrollBarHot(e.Location)) hit = 4;
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        // 正文上给 I 形光标，提示可以划选
        if (hit < 0 && e.Y > BarH && HitTextAt(e.Location, out _, out _)) Cursor = Cursors.IBeam;
    }

    private bool _scrollBarHot(Point p) => p.X >= ClientSize.Width - (int)Sc(14) && p.Y > BarH;

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != -1) { _hover = -1; Cursor = Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int hit = HitButton(e.Location);

        if (hit == 0) { OpenWithDialog(); return; }
        if (hit == 1) { WindowState = FormWindowState.Minimized; return; }
        if (hit == 2)
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
            return;
        }
        if (hit == 3) { Close(); return; }
        if (hit == 5) { CopyAll(); return; }
        if (hit >= 10)
        {
            SetTheme(hit - 10);
            return;
        }

        if (e.Button == MouseButtons.Left && e.Y > BarH && _layout is not null)
        {
            if (HitTextAt(e.Location, out int sop, out int sch))
            {
                _selAOp = _selBOp = sop;
                _selACh = _selBCh = sch;
                _selecting = true;
                _dragScroll = false;
                Cursor = Cursors.IBeam;
                Invalidate();
                return;
            }

            ClearSelection();
            _dragScroll = _scroll > 0 || MaxScroll(ClientSize.Height) > 0;
            _dragFrom = e.Y;
            _dragStart = _scroll;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragScroll = false;
        _selecting = false;
    }

    private bool _dragScroll;
    private int _dragFrom;
    private float _dragStart;

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        // Ctrl + 滚轮：缩放（以光标下的那一行为锚点，缩放后不跳位）
        if ((ModifierKeys & Keys.Control) != 0 && e.Delta != 0)
        {
            var before = GetLayout(ClientSize.Width, ClientSize.Height);
            float ratio = before.Height > 1 ? (_scroll + e.Y) / before.Height : 0f;

            int steps = e.Delta / 120;
            if (steps == 0) steps = e.Delta > 0 ? 1 : -1;
            _zoom = Math.Clamp(_zoom + 0.06f * steps, 0.7f, 1.8f);

            _layout = null;
            var after = GetLayout(ClientSize.Width, ClientSize.Height);
            _scroll = Math.Clamp(ratio * after.Height - e.Y, 0, MaxScroll(ClientSize.Height));
            Invalidate();
            return;
        }

        if (_layout is null) return;
        float max = MaxScroll(ClientSize.Height);
        _scroll = Math.Clamp(_scroll - e.Delta * Sc(0.9f), 0, max);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control && e.KeyCode == Keys.O) { OpenWithDialog(); return; }
        if (e.Control && e.Shift && e.KeyCode == Keys.C) { CopyAll(); return; }
        if (e.Control && (e.KeyCode == Keys.C || e.KeyCode == Keys.Insert))
        {
            if (HasSelection) CopySelection(); else CopyAll();
            return;
        }
        if (e.Control && e.KeyCode == Keys.A) { SelectAll(); return; }
        if (e.Control && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add))
        {
            _zoom = Math.Clamp(_zoom + 0.08f, 0.7f, 1.8f);
            Rebuild();
            return;
        }
        if (e.Control && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract))
        {
            _zoom = Math.Clamp(_zoom - 0.08f, 0.7f, 1.8f);
            Rebuild();
            return;
        }
        if (e.Control && e.KeyCode == Keys.D0)
        {
            _zoom = 1f;
            Rebuild();
            return;
        }
        if (e.Control && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D4)
        {
            SetTheme(e.KeyCode - Keys.D1);
            return;
        }

        float max = MaxScroll(ClientSize.Height);
        float page = ClientSize.Height - BarH - Sc(40);
        switch (e.KeyCode)
        {
            case Keys.PageDown: _scroll = Math.Clamp(_scroll + page, 0, max); break;
            case Keys.PageUp: _scroll = Math.Clamp(_scroll - page, 0, max); break;
            case Keys.Home: _scroll = 0; break;
            case Keys.End: _scroll = max; break;
            case Keys.Down: _scroll = Math.Clamp(_scroll + Sc(44), 0, max); break;
            case Keys.Up: _scroll = Math.Clamp(_scroll - Sc(44), 0, max); break;
            case Keys.Escape:
                if (HasSelection) { ClearSelection(); return; }
                WindowState = FormWindowState.Normal;
                return;
            default: return;
        }
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        DisposeBuffer();
        if (_layout is not null)
        {
            float max = MaxScroll(ClientSize.Height);
            _scroll = Math.Clamp(_scroll, 0, max);
            Rebuild();
        }
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            LoadDocument(files[0]);
    }

    /// <summary>把当前文档的 Markdown 源码整篇放进剪贴板（不是渲染结果，可直接粘进别的编辑器）。</summary>
    private void CopyAll()
    {
        string text = _rawText;
        if (string.IsNullOrEmpty(text) && _filePath.Length > 0)
        {
            try { text = DecodeText(File.ReadAllBytes(_filePath)); }
            catch { }
        }
        if (string.IsNullOrEmpty(text))
        {
            _notice = "没有可复制的正文";
            Invalidate();
            return;
        }
        try
        {
            PutText(text);
            _copied = true;
            _copyTimer.Stop();
            _copyTimer.Start();
            Invalidate();
        }
        catch (Exception ex)
        {
            _notice = "复制失败：" + ex.Message;
            Invalidate();
        }
    }

    /// <summary>写剪贴板：WinForms OLE 优先，校验失败再用 Win32 原生 API 兜底。</summary>
    private static void PutText(string text)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData(DataFormats.Text, text);
            Clipboard.SetDataObject(data, true);
            if (!string.IsNullOrEmpty(Clipboard.GetText())) return;
        }
        catch { }
        PutTextNative(text);
    }

    private static void PutTextNative(string text)
    {
        int bytes = (text.Length + 1) * 2;
        IntPtr h = Win32.GlobalAlloc(0x0042 /* GHND */, (UIntPtr)bytes);
        if (h == IntPtr.Zero) throw new Exception("GlobalAlloc 失败");
        bool owned = false;
        try
        {
            IntPtr p = Win32.GlobalLock(h);
            if (p == IntPtr.Zero) throw new Exception("GlobalLock 失败");
            byte[] buf = System.Text.Encoding.Unicode.GetBytes(text + "\0");
            Marshal.Copy(buf, 0, p, buf.Length);
            Win32.GlobalUnlock(h);

            if (!Win32.OpenClipboard(IntPtr.Zero)) throw new Exception("OpenClipboard 失败");
            try
            {
                Win32.EmptyClipboard();
                if (Win32.SetClipboardData(13 /* CF_UNICODETEXT */, h) == IntPtr.Zero)
                    throw new Exception("SetClipboardData 失败");
                owned = true; // 所有权交给系统，不能再 GlobalFree
            }
            finally { Win32.CloseClipboard(); }
        }
        finally
        {
            if (!owned) Win32.GlobalFree(h);
        }
    }

    // ---------- text selection ----------

    private bool HasSelection => _selAOp >= 0 && _selBOp >= 0 && (_selAOp != _selBOp || _selACh != _selBCh);

    private float WidthOf(DrawOp op, int chars)
    {
        int n = Math.Clamp(chars, 0, op.Text.Length);
        if (n == 0) return 0f;
        if (n == op.Text.Length) return op.W;
        return _measureG.MeasureString(op.Text.AsSpan(0, n).ToString(), op.Font, PointF.Empty,
            Layouter.Typographic).Width;
    }

    /// <summary>把客户区坐标换算成文档坐标，再判断落没落在某条文字指令上。</summary>
    private bool HitTextAt(Point p, out int opIdx, out int ch)
    {
        opIdx = -1; ch = 0;
        var layout = _layout;
        if (layout is null || p.Y <= BarH) return false;

        float dx = ContentLeft(ClientSize.Width);
        float dy = BarH - _scroll;
        float x = p.X - dx, y = p.Y - dy;

        foreach (var (op, i) in layout.Ops.Select((o, i) => (o, i)))
        {
            if (op.Kind != OpKind.Text) continue;
            if (y < op.Y || y >= op.Y + op.H) continue;
            if (x < op.X - 1f || x > op.X + op.W + 1f) continue;
            opIdx = i;
            ch = CharIndexAt(op, x - op.X);
            return true;
        }
        return false;
    }

    /// <summary>拖出文字范围时的兜底：取同一行里水平距离最近的文字，按左右吸附到行首/行尾。</summary>
    private bool NearestTextAt(Point p, out int opIdx, out int ch)
    {
        opIdx = -1; ch = 0;
        var layout = _layout;
        if (layout is null || p.Y <= BarH) return false;

        float dx = ContentLeft(ClientSize.Width);
        float dy = BarH - _scroll;
        float x = p.X - dx, y = p.Y - dy;

        int best = -1;
        float bestScore = float.MaxValue;
        for (int i = 0; i < layout.Ops.Count; i++)
        {
            var op = layout.Ops[i];
            if (op.Kind != OpKind.Text || op.H <= 0) continue;
            float cy = op.Y + op.H / 2f;
            if (Math.Abs(cy - y) > op.H * 1.1f) continue;
            float score = x < op.X ? op.X - x : (x > op.X + op.W ? x - op.X - op.W : 0f);
            if (score < bestScore) { bestScore = score; best = i; }
        }
        if (best < 0) return false;

        var b = layout.Ops[best];
        opIdx = best;
        ch = x <= b.X ? 0 : b.Text.Length;
        return true;
    }

    private int CharIndexAt(DrawOp op, float dx)
    {
        string s = op.Text;
        if (s.Length <= 1) return dx < op.W / 2f ? 0 : s.Length;

        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (WidthOf(op, mid) <= dx) lo = mid; else hi = mid - 1;
        }
        if (lo < s.Length)
        {
            float a = WidthOf(op, lo), b = WidthOf(op, lo + 1);
            if (dx > a + (b - a) / 2f) lo++;
        }
        return lo;
    }

    /// <summary>把锚点/焦点归一化成文档顺序的 (起点, 终点)。</summary>
    private bool SelOrdered(out int aOp, out int aCh, out int bOp, out int bCh)
    {
        aOp = bOp = -1; aCh = bCh = 0;
        if (_selAOp < 0 || _selBOp < 0) return false;
        if (_selAOp < _selBOp || (_selAOp == _selBOp && _selACh <= _selBCh))
        {
            aOp = _selAOp; aCh = _selACh; bOp = _selBOp; bCh = _selBCh;
        }
        else
        {
            aOp = _selBOp; aCh = _selBCh; bOp = _selAOp; bCh = _selACh;
        }
        return true;
    }

    private string SelectedText()
    {
        if (!SelOrdered(out int aOp, out int aCh, out int bOp, out int bCh)) return "";
        var layout = _layout;
        if (layout is null) return "";

        var sb = new System.Text.StringBuilder();
        float prevY = float.NaN, prevRight = 0;
        for (int i = aOp; i <= bOp && i < layout.Ops.Count; i++)
        {
            var op = layout.Ops[i];
            if (op.Kind != OpKind.Text) continue;
            int from = i == aOp ? aCh : 0;
            int to = i == bOp ? bCh : op.Text.Length;
            if (to <= from) continue;

            if (!float.IsNaN(prevY))
            {
                if (op.Y != prevY)
                {
                    while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                    sb.Append('\n');
                }
                else if (op.X - prevRight > 3f) sb.Append('\t');   // 表格单元格之间用制表符
            }
            sb.Append(op.Text, from, to - from);
            prevY = op.Y;
            prevRight = op.X + op.W;
        }
        return sb.ToString();
    }

    private void FlashCopied()
    {
        _copied = true;
        _copyTimer.Stop();
        _copyTimer.Start();
        Invalidate();
    }



    private void CopySelection()
    {
        string text = SelectedText();
        if (text.Length == 0) return;
        try
        {
            PutText(text);
            FlashCopied();
        }
        catch (Exception ex)
        {
            _notice = "复制失败：" + ex.Message;
            Invalidate();
        }
    }

    private void SelectAll()
    {
        var layout = _layout;
        if (layout is null) return;
        int first = -1, last = -1;
        for (int i = 0; i < layout.Ops.Count; i++)
        {
            if (layout.Ops[i].Kind != OpKind.Text) continue;
            if (first < 0) first = i;
            last = i;
        }
        if (first < 0) return;
        _selAOp = first; _selACh = 0;
        _selBOp = last; _selBCh = layout.Ops[last].Text.Length;
        Invalidate();
    }

    private void ClearSelection()
    {
        if (_selAOp < 0 && _selBOp < 0) return;
        _selAOp = _selBOp = -1;
        _selACh = _selBCh = 0;
        Invalidate();
    }

    /// <summary>画在文字之前：把选中区间按每条文字指令切段，填半透明强调色。</summary>
    private void DrawSelection(Graphics g, float dx, float dy)
    {
        if (!HasSelection || _layout is null) return;
        SelOrdered(out int aOp, out int aCh, out int bOp, out int bCh);

        using var br = new SolidBrush(Color.FromArgb(_theme.Dark ? 96 : 58, _theme.Accent));
        var ops = _layout.Ops;
        for (int i = aOp; i <= bOp && i < ops.Count; i++)
        {
            var op = ops[i];
            if (op.Kind != OpKind.Text) continue;
            int from = i == aOp ? aCh : 0;
            int to = i == bOp ? bCh : op.Text.Length;
            if (to <= from) continue;

            float x0 = op.X + WidthOf(op, from);
            float x1 = op.X + WidthOf(op, to);
            g.FillRectangle(br, x0 + dx, op.Y + dy, MathF.Max(1f, x1 - x0), op.H);
        }
    }

    private void OpenWithDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Markdown 文件|*.md;*.markdown;*.mdown;*.txt|所有文件|*.*",
            Title = "打开 Markdown",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) LoadDocument(dlg.FileName);
    }

    public void SetThemePublic(int index) => SetTheme(index);

    public void SetZoomPublic(float zoom)
    {
        _zoom = Math.Clamp(zoom, 0.7f, 1.8f);
        Rebuild();
    }

    private void SetTheme(int index)
    {
        if (index < 0 || index >= Theme.All.Length) return;
        _themeIndex = index;
        _theme = Theme.All[index];
        ApplyDwm();
        Rebuild();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeBuffer();
            _measureG.Dispose();
            _measureBmp.Dispose();
        }
        base.Dispose(disposing);
    }
}
