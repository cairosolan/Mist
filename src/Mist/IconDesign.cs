using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Mist;

/// <summary>
/// 程序图标与 .md 文件图标的几何绘制。用代码画而不是位图，是为了让 16/20/24/32 这些
/// 小尺寸也能保持锐利——AI 生成的图缩到 16px 会糊成一团。
/// 由 <c>Mist.exe --makeicons &lt;dir&gt;</c> 调用，产出多尺寸 .ico。
/// </summary>
internal static class IconDesign
{
    public static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = Math.Min(r * 2, Math.Min(w, h));
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>几何 M：竖腿用矩形，斜线用等宽画笔（perp 等粗），顶点补一个圆使尖角不缺口。</summary>
    private static void DrawM(Graphics g, float x0, float y0, float w, float h, float t, Color color)
    {
        using var br = new SolidBrush(color);
        float cx = x0 + w / 2f;
        float apexY = y0 + h * 0.58f;

        // 两条竖腿
        g.FillRectangle(br, x0, y0, t, h);
        g.FillRectangle(br, x0 + w - t, y0, t, h);

        // 两条斜线：从各自腿的顶部中点出发，收到同一个顶点
        using (var pen = new Pen(color, t) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
        {
            g.DrawLine(pen, x0 + t / 2f, y0 + t / 2f, cx, apexY);
            g.DrawLine(pen, x0 + w - t / 2f, y0 + t / 2f, cx, apexY);
        }
        // 顶点圆滑收口
        g.FillEllipse(br, cx - t / 2f, apexY - t / 2f, t, t);
    }

    private static void DrawArrowDown(Graphics g, float cx, float top, float bottom, float t, Color color)
    {
        using var br = new SolidBrush(color);
        float bar = t * 0.80f;
        float headW = t * 1.75f;
        float headH = t * 1.35f;
        g.FillRectangle(br, cx - bar / 2f, top, bar, bottom - top - headH * 0.55f);
        g.FillPolygon(br, new[]
        {
            new PointF(cx - headW, bottom - headH),
            new PointF(cx + headW, bottom - headH),
            new PointF(cx, bottom),
        });
    }

    /// <summary>kind = "app"（Mist 本体）或 "doc"（.md 文档，M↓）。</summary>
    public static Bitmap Render(string kind, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float S = size;
        float pad = S * 0.0703f;
        float tile = S - pad * 2;
        float rad = tile * 0.223f;

        bool doc = kind == "doc";
        float sw = Math.Max(1f, S / 340f);

        // ---- 底色 ----
        if (doc)
        {
            // 文件图标用「纸页」轮廓而不是方块：16px 下也能一眼看出是文件
            float pw = tile * 0.735f, ph = tile * 0.955f;
            float px = pad + (tile - pw) / 2f, py = pad + (tile - ph) / 2f;
            float fold = pw * 0.235f;
            float pr = pw * 0.075f;
            var pagePath = new GraphicsPath();
            pagePath.AddArc(px, py + ph - pr * 2, pr * 2, pr * 2, 90, 90);        // 左下
            pagePath.AddArc(px, py, pr * 2, pr * 2, 180, 90);                     // 左上
            pagePath.AddLine(px + pr, py, px + pw - fold, py);                    // 上边（到折角）
            pagePath.AddLine(px + pw - fold, py, px + pw, py + fold);             // 折角右斜边
            pagePath.AddLine(px + pw, py + fold, px + pw, py + ph - pr);          // 右边
            pagePath.AddArc(px + pw - pr * 2, py + ph - pr * 2, pr * 2, pr * 2, 0, 90); // 右下
            pagePath.AddLine(px + pw - pr, py + ph, px + pr, py + ph);            // 下边
            pagePath.CloseFigure();

            using var lg = new LinearGradientBrush(
                new RectangleF(px, py, pw, ph + 1),
                Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 243, 237, 227),
                LinearGradientMode.Vertical);
            var blend = new ColorBlend(3)
            {
                Positions = new[] { 0f, 0.72f, 1f },
                Colors = new[]
                {
                    Color.FromArgb(255, 255, 255, 255),
                    Color.FromArgb(255, 250, 246, 238),
                    Color.FromArgb(255, 238, 231, 219),
                },
            };
            lg.InterpolationColors = blend;
            g.FillPath(lg, pagePath);

            using (var pen = new Pen(Color.FromArgb(205, 176, 166, 148), sw))
                g.DrawPath(pen, pagePath);

            // 折角
            using (var foldBrush = new SolidBrush(Color.FromArgb(255, 242, 236, 224)))
                g.FillPolygon(foldBrush, new[]
                {
                    new PointF(px + pw - fold, py),
                    new PointF(px + pw - fold, py + fold),
                    new PointF(px + pw, py + fold),
                });
            using (var pen = new Pen(Color.FromArgb(120, 176, 166, 148), sw))
            {
                g.DrawLine(pen, px + pw - fold, py, px + pw - fold, py + fold);
                g.DrawLine(pen, px + pw - fold, py + fold, px + pw, py + fold);
            }

            // 纸张上的 M↓
            float capH0 = ph * 0.270f;
            float capW0 = pw * 0.330f;
            float stroke0 = capW0 * (size <= 20 ? 0.300f : size <= 32 ? 0.260f : 0.215f);
            float gap0 = pw * 0.075f;
            float arrowW0 = pw * 0.150f;
            float totalW0 = capW0 + gap0 + arrowW0;
            float gx0 = px + (pw - totalW0) / 2f;
            float gy0 = py + ph * 0.455f - capH0 / 2f;
            DrawM(g, gx0, gy0, capW0, capH0, stroke0, Color.FromArgb(255, 26, 24, 21));
            if (size > 24)
                DrawArrowDown(g, gx0 + capW0 + gap0 + arrowW0 / 2f, gy0, gy0 + capH0, stroke0 * 1.10f,
                    Color.FromArgb(255, 26, 24, 21));

            // 底部两行「正文」提示线，暗示这是一份文档
            if (size >= 32)
            {
                float lineH = Math.Max(1f, ph * 0.018f);
                using var br = new SolidBrush(Color.FromArgb(120, 90, 84, 72));
                float lx = px + pw * 0.16f, lw = pw * 0.68f;
                g.FillRectangle(br, lx, py + ph * 0.700f, lw, lineH);
                g.FillRectangle(br, lx, py + ph * 0.756f, lw * 0.55f, lineH);
            }
            return bmp;
        }

        Color top = Color.FromArgb(255, 255, 255, 255);
        Color bottom = Color.FromArgb(255, 211, 223, 240);
        Color edge = Color.FromArgb(216, 175, 190, 212);
        Color ink = Color.FromArgb(255, 43, 92, 200);

        var rect = new RectangleF(pad, pad, tile, tile);
        using (var path = RoundRect(rect.X, rect.Y, rect.Width, rect.Height, rad))
        {
            // 一整条多段渐变更平滑：顶部纯白 → 中段淡蓝 → 底部略压深，一次填完不留色带
            using var lg = new LinearGradientBrush(
                new RectangleF(rect.X, rect.Y, rect.Width, rect.Height + 1),
                top, bottom, LinearGradientMode.Vertical);
            var blend = new ColorBlend(4)
            {
                Positions = new[] { 0f, 0.45f, 0.78f, 1f },
                Colors = new[]
                {
                    top,
                    BlendC(top, bottom, 0.42f),
                    BlendC(top, bottom, 0.80f),
                    Color.FromArgb(255, 200, 214, 233),
                },
            };
            lg.InterpolationColors = blend;
            g.FillPath(lg, path);

            using var pen = new Pen(edge, sw);
            g.DrawPath(pen, path);

            float inset = sw * 2.2f;
            using var hp = RoundRect(rect.X + inset, rect.Y + inset,
                rect.Width - inset * 2, rect.Height - inset * 2, Math.Max(1f, rad - inset));
            using var hpen = new Pen(Color.FromArgb(105, 255, 255, 255), sw * 1.6f);
            g.DrawPath(hpen, hp);
        }

        float capH = tile * 0.470f;
        float capW = tile * 0.420f;
        float stroke = capW * (size <= 20 ? 0.290f : size <= 32 ? 0.250f : 0.205f);
        DrawM(g, pad + (tile - capW) / 2f, pad + (tile - capH) / 2f, capW, capH, stroke, ink);

        return bmp;
    }

    private static Color BlendC(Color a, Color b, float k) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * k), (int)(a.R + (b.R - a.R) * k),
        (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

    private static byte[] DibBytes(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var ms = new MemoryStream();
        var bi = new byte[40];
        BitConverter.GetBytes(40).CopyTo(bi, 0);
        BitConverter.GetBytes(w).CopyTo(bi, 4);
        BitConverter.GetBytes(h * 2).CopyTo(bi, 8);
        BitConverter.GetBytes((short)1).CopyTo(bi, 12);
        BitConverter.GetBytes((short)32).CopyTo(bi, 14);
        BitConverter.GetBytes(w * h * 4).CopyTo(bi, 20);
        ms.Write(bi, 0, 40);

        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                {
                    if (row[x * 4 + 3] != 0) continue;
                    row[x * 4 + 0] = row[x * 4 + 1] = row[x * 4 + 2] = 0;
                }
                ms.Write(row, 0, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }

        int maskRow = (w + 31) / 32 * 4;
        var zeros = new byte[maskRow];
        for (int y = 0; y < h; y++) ms.Write(zeros, 0, maskRow);
        return ms.ToArray();
    }

    /// <summary>多尺寸 .ico：小尺寸用 DIB（兼容性最好），256 用 PNG。</summary>
    public static void WriteIco(string kind, string path, int[] sizes)
    {
        var images = new List<(int size, byte[] bytes)>();
        foreach (int s in sizes)
        {
            using var bmp = Render(kind, s);
            byte[] bytes;
            if (s >= 256)
            {
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                bytes = ms.ToArray();
            }
            else bytes = DibBytes(bmp);
            images.Add((s, bytes));
        }

        var header = new byte[6 + images.Count * 16];
        BitConverter.GetBytes((short)0).CopyTo(header, 0);
        BitConverter.GetBytes((short)1).CopyTo(header, 2);
        BitConverter.GetBytes((short)images.Count).CopyTo(header, 4);
        int offset = header.Length;
        for (int i = 0; i < images.Count; i++)
        {
            int b = 6 + i * 16;
            byte dim = (byte)(images[i].size >= 256 ? 0 : images[i].size);
            header[b] = dim;
            header[b + 1] = dim;
            BitConverter.GetBytes((short)1).CopyTo(header, b + 4);
            BitConverter.GetBytes((short)32).CopyTo(header, b + 6);
            BitConverter.GetBytes(images[i].bytes.Length).CopyTo(header, b + 8);
            BitConverter.GetBytes(offset).CopyTo(header, b + 12);
            offset += images[i].bytes.Length;
        }

        using var fs = File.Create(path);
        fs.Write(header, 0, header.Length);
        foreach (var im in images) fs.Write(im.bytes, 0, im.bytes.Length);
    }

    /// <summary>核对图：浅底 1x / 深底 1x / 灰底 2x，三排看不同尺寸的可读性。</summary>
    public static void Sheet(string kind, string path, int[] sizes)
    {
        const int pitch = 132, W = pitch * 4 + 12, H = 372;
        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        using (var b = new SolidBrush(Color.FromArgb(255, 250, 250, 252))) g.FillRectangle(b, 0, 0, W, 124);
        using (var b = new SolidBrush(Color.FromArgb(255, 22, 24, 28))) g.FillRectangle(b, 0, 124, W, 124);
        using (var b = new SolidBrush(Color.FromArgb(255, 128, 132, 140))) g.FillRectangle(b, 0, 248, W, 124);

        for (int i = 0; i < sizes.Length && i < 4; i++)
        {
            int s = sizes[i];
            float cx = 12 + pitch * i + pitch / 2f;
            using var b1 = Render(kind, s);
            g.DrawImage(b1, cx - s / 2f, 62 - s / 2f, s, s);
            g.DrawImage(b1, cx - s / 2f, 186 - s / 2f, s, s);
            using var b2 = Render(kind, s);
            g.DrawImage(b2, cx - s, 310 - s, s * 2, s * 2);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    public static void Emit(string dir)
    {
        Directory.CreateDirectory(dir);
        WriteIco("app", Path.Combine(dir, "Mist.ico"), Sizes);
        WriteIco("doc", Path.Combine(dir, "MistDoc.ico"), Sizes);

        using (var b = Render("app", 1024)) b.Save(Path.Combine(dir, "preview-Mist-1024.png"), ImageFormat.Png);
        using (var b = Render("doc", 1024)) b.Save(Path.Combine(dir, "preview-MistDoc-1024.png"), ImageFormat.Png);
        Sheet("app", Path.Combine(dir, "icon-check-app.png"), new[] { 16, 24, 32, 48 });
        Sheet("doc", Path.Combine(dir, "icon-check-doc.png"), new[] { 16, 24, 32, 48 });
    }
}


