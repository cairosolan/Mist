using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Mist;

internal enum OpKind { Fill, Text, Line }

internal struct DrawOp
{
    public OpKind Kind;
    public float X, Y, W, H;
    public string Text;
    public Font Font;
    public Color Color;
    public float Radius;
    public float Top => Y;
    public float Bottom => Y + H;
}

internal sealed class DocLayout
{
    public List<DrawOp> Ops = new();
    public float Height;
    public float ContentWidth;
}

internal sealed class FontCache
{
    private readonly Dictionary<(string, float, int), Font> _map = new();

    public Font Get(string family, float px, FontStyle style)
    {
        var key = (family, MathF.Round(px, 2), (int)style);
        if (!_map.TryGetValue(key, out var f))
        {
            f = new Font(family, MathF.Max(1f, px), style, GraphicsUnit.Pixel);
            _map[key] = f;
        }
        return f;
    }
}

/// <summary>Turns parsed markdown blocks into absolutely positioned draw ops.</summary>
internal sealed class Layouter
{
    private readonly Theme _th;
    private readonly FontCache _fonts;
    private readonly Graphics _mg;
    private readonly List<DrawOp> _ops = new();

    private float _k = 1f;
    private float _x, _w, _y;

    private static readonly StringFormat Typo = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap
    };

    public Layouter(Theme theme, FontCache fonts, Graphics measureGraphics)
    {
        _th = theme;
        _fonts = fonts;
        _mg = measureGraphics;
    }

    public static StringFormat Typographic => Typo;

    private float S(float v) => v * _k;

    public DocLayout Build(List<Block> blocks, float contentWidth, float scale, float zoom, float padTop, float padBottom)
    {
        _k = scale * zoom;
        _ops.Clear();
        _w = contentWidth;
        _x = 0;
        _y = padTop;

        foreach (var b in blocks) LayoutBlock(b);

        _y += padBottom;
        return new DocLayout { Ops = _ops, Height = _y, ContentWidth = _w };
    }

    private void LayoutBlock(Block b)
    {
        switch (b.Kind)
        {
            case BlockKind.Heading:
                LayoutHeading(b);
                break;
            case BlockKind.Paragraph:
                Flow(b.Runs, 15.5f, 1.58f);
                _y += S(11);
                break;
            case BlockKind.Quote:
                LayoutQuote(b);
                break;
            case BlockKind.Bullet:
            case BlockKind.Ordered:
                LayoutListItem(b);
                break;
            case BlockKind.Code:
                LayoutCode(b);
                break;
            case BlockKind.Table:
                LayoutTable(b);
                break;
            case BlockKind.Rule:
                _y += S(14);
                AddLine(_x, _y, _w, S(1), _th.Rule);
                _y += S(20);
                break;
        }
    }

    private void LayoutHeading(Block b)
    {
        float size = b.Level switch { 1 => 28.5f, 2 => 21.5f, 3 => 18f, 4 => 16.2f, 5 => 15.2f, 6 => 14.2f, _ => 15.5f };
        float before = b.Level switch { 1 => 6, 2 => 20, 3 => 16, 4 => 13, 5 => 10, 6 => 9, _ => 9 };
        float after = b.Level switch { 1 => 12, 2 => 8, 3 => 7, 4 => 6, 5 => 5, 6 => 5, _ => 5 };
        if (_y > S(8)) _y += S(before);
        Flow(b.Runs, size, 1.22f, bold: true);
        _y += S(after);
    }

    private void LayoutQuote(Block b)
    {
        _y += S(6);
        float startY = _y;
        float indent = S(16);
        Flow(b.Runs, 15f, 1.52f, colorOverride: _th.Muted, xOffset: indent);
        float h = _y - startY;
        AddFill(_x, startY + S(1), S(2.6f), MathF.Max(S(10), h - S(4)), _th.Muted, 0);
        _y += S(12);
    }

    private void LayoutListItem(Block b)
    {
        _y += S(1);
        float indent = S(20 * (b.Level + 1));
        float markerW = S(b.Kind == BlockKind.Ordered ? 22 : 16);
        float startY = _y;

        // marker on its own, aligned to the first text line
        var mf = _fonts.Get(b.Kind == BlockKind.Ordered ? "Segoe UI" : "Microsoft YaHei UI", S(15f), FontStyle.Regular);
        var mc = b.Kind == BlockKind.Ordered ? _th.Muted : _th.Text;
        float y0 = _y;
        AddText(b.Marker, mf, mc, _x + indent, y0, mf.GetHeight(_mg) * 1.46f);

        Flow(b.Runs, 15.2f, 1.46f, xOffset: indent + markerW);
        _y += S(3);
    }

    private void LayoutCode(Block b)
    {
        _y += S(4);
        float pad = S(15);
        float size = S(14.2f);
        // 深色主题下 Consolas 常规字重在暗底上偏细，加粗一档才看得清
        var f = _fonts.Get("Consolas", size, _th.Dark ? FontStyle.Bold : FontStyle.Regular);
        float lineH = f.GetHeight(_mg) * 1.42f;

        float textW = _w - pad * 2;

        var rows = new List<string>();
        foreach (var raw in b.Raw.Split('\n'))
        {
            string line = raw.Replace("\t", "    ");
            rows.AddRange(BreakToWidth(line, f, textW));
        }

        float boxH = pad * 2 + lineH * rows.Count;
        AddFill(_x, _y, _w, boxH, _th.CodeBg, S(9));
        if (_th.CodeBorder.A > 0) AddStroke(_x, _y, _w, boxH, S(9), _th.CodeBorder);

        float tx = _x + pad;
        float ty = _y + pad;
        foreach (var r in rows)
        {
            AddText(r, f, _th.CodeText, tx, ty, lineH);
            ty += lineH;
        }
        _y += boxH + S(16);
    }

    /// <summary>表格：列宽按内容自然宽度分配，剩余空间按比例摊开撑满版心。</summary>
    private void LayoutTable(Block b)
    {
        const float size = 14.4f;
        float padH = S(11), padV = S(8);
        int cols = b.Align.Count;

        float lineH = _fonts.Get("Microsoft YaHei UI", S(size), FontStyle.Regular).GetHeight(_mg) * 1.5f;

        var headToks = new List<List<Tok>>(cols);
        for (int c = 0; c < cols; c++)
            headToks.Add(BuildTokens(MarkdownParser.ParseInline(b.Cells[0][c]), size, bold: true));

        var bodyToks = new List<List<List<Tok>>>();
        for (int r = 1; r < b.Cells.Count; r++)
        {
            var row = new List<List<Tok>>(cols);
            for (int c = 0; c < cols; c++)
                row.Add(BuildTokens(MarkdownParser.ParseInline(b.Cells[r][c]), size));
            bodyToks.Add(row);
        }

        // 自然列宽：该列最宽的单元格（含内边距），上限为版心的 58%，避免一列吃满
        var natW = new float[cols];
        for (int c = 0; c < cols; c++)
        {
            float mx = SumW(headToks[c]);
            foreach (var row in bodyToks) mx = MathF.Max(mx, SumW(row[c]));
            natW[c] = MathF.Min(mx + padH * 2, _w * 0.58f);
        }

        var colW = new float[cols];
        float total = 0;
        foreach (var v in natW) total += v;
        if (total <= 0) return;

        if (total > _w)
        {
            float k = _w / total;
            for (int c = 0; c < cols; c++) colW[c] = MathF.Max(S(44), natW[c] * k);
            float sum = 0;
            foreach (var v in colW) sum += v;
            if (sum > _w)
            {
                float k2 = _w / sum;
                for (int c = 0; c < cols; c++) colW[c] *= k2;
            }
        }
        else
        {
            float extra = _w - total;
            for (int c = 0; c < cols; c++) colW[c] = natW[c] + extra * (natW[c] / total);
        }

        // 折行只算一次，高度和绘制共用
        var headLines = WrapRow(headToks, colW, padH);
        var bodyLines = new List<List<List<List<Tok>>>>(bodyToks.Count);
        foreach (var row in bodyToks) bodyLines.Add(WrapRow(row, colW, padH));

        float x0 = _x;
        _y += S(6);

        // 表头：淡色底 + 一条分隔线
        float headH = RowHeight(headLines, padV, lineH);
        AddFill(x0, _y, _w, headH, _th.Hover, 0);
        DrawRow(headLines, colW, b.Align, x0, _y, padH, padV, lineH);
        _y += headH;
        AddLine(x0, _y, _w, S(1), _th.Rule);
        _y += S(1);

        // 表体：行间细线
        for (int r = 0; r < bodyLines.Count; r++)
        {
            float h = RowHeight(bodyLines[r], padV, lineH);
            DrawRow(bodyLines[r], colW, b.Align, x0, _y, padH, padV, lineH);
            _y += h;
            if (r < bodyLines.Count - 1)
            {
                AddLine(x0, _y, _w, S(1), Color.FromArgb(46, _th.Text));
                _y += S(1);
            }
        }

        _y += S(18);
    }

    private float SumW(List<Tok> toks)
    {
        float w = 0;
        foreach (var t in toks) w += t.W;
        return w;
    }

    private List<List<List<Tok>>> WrapRow(List<List<Tok>> cells, float[] colW, float padH)
    {
        var res = new List<List<List<Tok>>>(cells.Count);
        for (int c = 0; c < cells.Count; c++)
            res.Add(WrapTokens(cells[c], colW[c] - padH * 2));
        return res;
    }

    private static float RowHeight(List<List<List<Tok>>> linesByCol, float padV, float lineH)
    {
        int maxLines = 1;
        foreach (var lines in linesByCol) maxLines = Math.Max(maxLines, lines.Count);
        return padV * 2 + lineH * maxLines;
    }

    private void DrawRow(List<List<List<Tok>>> linesByCol, float[] colW, List<char> align,
        float x0, float y, float padH, float padV, float lineH)
    {
        float x = x0;
        for (int c = 0; c < linesByCol.Count; c++)
        {
            var lines = linesByCol[c];
            float avail = colW[c] - padH * 2;
            for (int li = 0; li < lines.Count; li++)
            {
                var line = lines[li];
                float lw = SumW(line);
                float lx = x + padH;
                if (align[c] == 'c') lx += (avail - lw) / 2f;
                else if (align[c] == 'r') lx += avail - lw;

                float ly = y + padV + li * lineH;
                foreach (var t in line)
                {
                    AddText(t.T, t.F, t.C, lx, ly, lineH);
                    lx += t.W;
                }
            }
            x += colW[c];
        }
    }

    private sealed class Tok
    {
        public string T = "";
        public Font F = null!;
        public Color C;
        public float W;
    }

    /// <summary>按真实像素宽度折行。等宽字体里中文是全角，按字符数折行必然出框。</summary>
    private IEnumerable<string> BreakToWidth(string line, Font f, float avail)
    {
        if (line.Length == 0) { yield return ""; yield break; }
        if (_mg.MeasureString(line, f, PointF.Empty, Typo).Width <= avail) { yield return line; yield break; }

        var cache = new Dictionary<char, float>();
        int start = 0;
        float w = 0;
        for (int i = 0; i < line.Length; i++)
        {
            float cw;
            if (!cache.TryGetValue(line[i], out cw))
            {
                cw = _mg.MeasureString(line[i].ToString(), f, PointF.Empty, Typo).Width;
                cache[line[i]] = cw;
            }
            if (w + cw > avail && i > start)
            {
                yield return line[start..i];
                start = i;
                w = 0;
            }
            w += cw;
        }
        if (start < line.Length) yield return line[start..];
    }

    private List<Tok> BuildTokens(List<Run> runs, float size, bool bold = false, Color? colorOverride = null)
    {
        var toks = new List<Tok>();
        foreach (var r in runs)
        {
            var style = FontStyle.Regular;
            if (r.Bold || bold) style |= FontStyle.Bold;
            if (r.Italic) style |= FontStyle.Italic;

            Color color;
            Font mono = null!;
            if (r.Code)
            {
                mono = _fonts.Get("Consolas", S(14.0f),
                    _th.Dark ? (style | FontStyle.Bold) : style);
                color = _th.CodeText;
            }
            else
            {
                color = r.Link ? _th.Accent : (colorOverride ?? _th.Text);
            }

            foreach (var (seg, isCjk) in SplitSegments(r.Text))
            {
                var f = r.Code ? mono : _fonts.Get(isCjk ? "Microsoft YaHei UI" : "Segoe UI", S(size), style);
                float w = _mg.MeasureString(seg, f, PointF.Empty, Typo).Width;
                toks.Add(new Tok { T = seg, F = f, C = color, W = w });
            }
        }
        return toks;
    }

    private List<List<Tok>> WrapTokens(List<Tok> toks, float maxW)
    {
        var lines = new List<List<Tok>>();
        var cur = new List<Tok>();
        float curW = 0;
        foreach (var t in toks)
        {
            float w = t.W;
            // 行首禁则：标点不另起一行，挤在行末（允许半字溢出）
            bool keepWithPrev = t.T.Length == 1 && IsNoLineStart(t.T[0]);
            if (cur.Count > 0 && curW + w > maxW + 0.5f && !keepWithPrev)
            {
                // 行尾禁则：开引号/开括号不作行尾，跟随下一行
                var carry = new List<Tok>();
                while (cur.Count > 1 && IsNoLineEnd(cur[cur.Count - 1].T))
                {
                    carry.Insert(0, cur[cur.Count - 1]);
                    cur.RemoveAt(cur.Count - 1);
                }
                lines.Add(cur);
                cur = carry;
                curW = 0;
                foreach (var c in cur) curW += c.W;
            }
            if (cur.Count == 0 && t.T.Length > 0 && char.IsWhiteSpace(t.T[0]))
            {
                t.T = t.T.TrimStart();
                t.W = _mg.MeasureString(t.T, t.F, PointF.Empty, Typo).Width;
                if (t.T.Length == 0) continue;
            }
            cur.Add(t);
            curW += w;
        }
        if (cur.Count > 0) lines.Add(cur);
        return lines;
    }

    private void Flow(List<Run> runs, float size, float lineGap, bool bold = false,
        Color? colorOverride = null, float xOffset = 0f)
    {
        float maxW = _w - xOffset;
        float baseX = _x + xOffset;

        var toks = BuildTokens(runs, size, bold, colorOverride);
        var lines = WrapTokens(toks, maxW);

        foreach (var line in lines)
        {
            float lh = 0;
            foreach (var t in line) lh = MathF.Max(lh, t.F.GetHeight(_mg));
            lh *= lineGap;

            float x = baseX;
            foreach (var t in line)
            {
                _ops.Add(new DrawOp
                {
                    Kind = OpKind.Text,
                    Text = t.T,
                    Font = t.F,
                    Color = t.C,
                    X = x,
                    Y = _y,
                    H = lh,
                    W = t.W,
                });
                x += t.W;
            }
            _y += lh;
        }
    }

    /// <summary>Split into wrap units: CJK chars on their own, latin words with trailing spaces.</summary>
    private static IEnumerable<(string, bool)> SplitSegments(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (IsCjk(c))
            {
                yield return (s.Substring(i, 1), true);
                i++;
                continue;
            }
            int start = i;
            while (i < s.Length && !IsCjk(s[i]) && s[i] != ' ') i++;
            while (i < s.Length && s[i] == ' ') i++;
            yield return (s[start..i], false);
        }
    }

    private static bool IsCjk(char c) =>
        (c >= 0x2E80 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF00 && c <= 0xFFEF) || (c >= 0x3000 && c <= 0x303F);

    private static readonly char[] NoLineStart =
        { '，', '。', '、', '；', '：', '！', '？', '）', '》', '」', '』', '】', '”', '’', '…', '·', '～',
          ',', '.', ';', ':', '!', '?', ')', ']', '}', '%' };

    private static readonly char[] NoLineEnd =
        { '（', '《', '「', '『', '【', '“', '‘', '(', '[', '{' };

    private static bool IsNoLineStart(char c) => Array.IndexOf(NoLineStart, c) >= 0;
    private static bool IsNoLineEnd(string s) => s.Length > 0 && Array.IndexOf(NoLineEnd, s[s.Length - 1]) >= 0;

    private void AddText(string text, Font f, Color c, float x, float y, float h)
    {
        if (string.IsNullOrEmpty(text)) return;
        _ops.Add(new DrawOp { Kind = OpKind.Text, Text = text, Font = f, Color = c, X = x, Y = y, H = h, W = 0 });
    }

    private void AddFill(float x, float y, float w, float h, Color c, float r)
    {
        if (w <= 0 || h <= 0) return;
        _ops.Add(new DrawOp { Kind = OpKind.Fill, X = x, Y = y, W = w, H = h, Color = c, Radius = r });
    }

    private void AddStroke(float x, float y, float w, float h, float r, Color c)
    {
        _ops.Add(new DrawOp { Kind = OpKind.Line, X = x, Y = y, W = w, H = h, Color = c, Radius = r });
    }

    private void AddLine(float x, float y, float w, float h, Color c)
    {
        _ops.Add(new DrawOp { Kind = OpKind.Fill, X = x, Y = y, W = w, H = h, Color = c, Radius = 0 });
    }
}
