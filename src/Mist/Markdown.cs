using System.Text.RegularExpressions;

namespace Mist;

internal enum BlockKind { Heading, Paragraph, Code, Quote, Bullet, Ordered, Rule, Table }

internal sealed class Block
{
    public BlockKind Kind;
    public int Level;              // heading level, or list indent depth
    public string Marker = "";     // "•" / "1."
    public string Lang = "";
    public string Raw = "";        // code block body
    public List<Run> Runs = new();
    /// <summary>Table only: rows of cells, row 0 is the header.</summary>
    public List<List<string>> Cells = new();
    /// <summary>Table only: per-column alignment 'l' | 'c' | 'r'.</summary>
    public List<char> Align = new();
}

internal sealed class Run
{
    public string Text = "";
    public bool Bold, Italic, Code, Link;
}

internal static class MarkdownParser
{
    private static readonly Regex ListRx = new(@"^(\s*)([-*+]|(\d{1,3})[.)])\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex HeadingRx = new(@"^(\s*)(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex RuleRx = new(@"^\s*([-*_])\s*(\1\s*){2,}$", RegexOptions.Compiled);

    public static List<Block> Parse(string src)
    {
        src = src.Replace("\r\n", "\n").Replace('\r', '\n');
        if (src.Length > 0 && src[0] == '\uFEFF') src = src[1..];
        var lines = src.Split('\n');
        var blocks = new List<Block>();
        var pending = new List<string>();

        void FlushParagraph()
        {
            if (pending.Count == 0) return;
            var joined = string.Join(" ", pending.Select(p => p.Trim())).Trim();
            pending.Clear();
            if (joined.Length == 0) return;
            blocks.Add(new Block { Kind = BlockKind.Paragraph, Runs = ParseInline(joined) });
        }

        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            string trimmed = line.TrimEnd();

            // fenced code
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                FlushParagraph();
                string fence = trimmed[..3];
                string lang = trimmed[3..].Trim();
                i++;
                var body = new List<string>();
                while (i < lines.Length && !lines[i].TrimEnd().StartsWith(fence)) { body.Add(lines[i]); i++; }
                if (i < lines.Length) i++;
                blocks.Add(new Block { Kind = BlockKind.Code, Lang = lang, Raw = string.Join("\n", body).TrimEnd('\n'), Level = 0 });
                continue;
            }

            if (trimmed.Trim().Length == 0) { FlushParagraph(); i++; continue; }

            var hm = HeadingRx.Match(trimmed);
            if (hm.Success)
            {
                FlushParagraph();
                int level = hm.Groups[2].Value.Length;
                string text = hm.Groups[3].Value.Trim().TrimEnd('#').Trim();
                blocks.Add(new Block { Kind = BlockKind.Heading, Level = level, Runs = ParseInline(text) });
                i++;
                continue;
            }

            if (RuleRx.IsMatch(trimmed)) { FlushParagraph(); blocks.Add(new Block { Kind = BlockKind.Rule }); i++; continue; }

            // blockquote (consecutive lines merge)
            if (trimmed.TrimStart().StartsWith(">"))
            {
                FlushParagraph();
                var parts = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">"))
                {
                    parts.Add(lines[i].TrimStart()[1..].Trim());
                    i++;
                }
                var joined = string.Join(" ", parts).Trim();
                if (joined.Length > 0)
                    blocks.Add(new Block { Kind = BlockKind.Quote, Runs = ParseInline(joined) });
                continue;
            }

            var lm = ListRx.Match(trimmed);
            if (lm.Success)
            {
                FlushParagraph();
                int indent = lm.Groups[1].Value.Replace("\t", "  ").Length;
                bool ordered = lm.Groups[3].Success;
                string marker = ordered ? lm.Groups[3].Value + "." : "•";
                string text = lm.Groups[4].Value.Trim();
                blocks.Add(new Block
                {
                    Kind = ordered ? BlockKind.Ordered : BlockKind.Bullet,
                    Level = Math.Min(3, indent / 2),
                    Marker = marker,
                    Runs = ParseInline(text)
                });
                i++;
                continue;
            }

            // table: header row + |---| separator row, then body rows
            if (trimmed.Trim().StartsWith("|") && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushParagraph();
                var head = SplitRow(trimmed);
                var align = new List<char>();
                foreach (var cell in SplitRow(lines[i + 1]))
                {
                    string c = cell.Trim();
                    bool left = c.StartsWith(":"), right = c.EndsWith(":");
                    align.Add(left && right ? 'c' : right ? 'r' : 'l');
                }
                i += 2;
                var body = new List<List<string>>();
                while (i < lines.Length && lines[i].Trim().StartsWith("|"))
                {
                    body.Add(SplitRow(lines[i]));
                    i++;
                }
                var cells = new List<List<string>> { head };
                cells.AddRange(body);

                // 列数按最大行补齐，缺的格子留空
                int cols = cells.Max(r => r.Count);
                foreach (var row in cells)
                    while (row.Count < cols) row.Add("");
                while (align.Count < cols) align.Add('l');

                blocks.Add(new Block { Kind = BlockKind.Table, Cells = cells, Align = align });
                continue;
            }

            // 不是表格的管道行，退回普通段落，不再塞进等宽块
            if (trimmed.Contains('|') && trimmed.Trim().StartsWith("|"))
            {
                pending.Add(trimmed);
                i++;
                continue;
            }

            pending.Add(trimmed);
            i++;
        }
        FlushParagraph();
        return blocks;
    }

    /// <summary>| --- | :--- | ---: | 这样的分隔行，决定每列对齐方式。</summary>
    private static bool IsTableSeparator(string line)
    {
        string t = line.Trim();
        if (!t.StartsWith("|")) return false;
        var cells = SplitRow(t);
        if (cells.Count == 0) return false;
        foreach (var c in cells)
        {
            var core = c.Trim().Trim(':');
            if (core.Length < 3) return false;
            foreach (var ch in core) if (ch != '-') return false;
        }
        return true;
    }

    /// <summary>按 | 拆单元格：忽略反引号里的竖线，支持 \| 转义。</summary>
    private static List<string> SplitRow(string line)
    {
        string t = line.Trim();
        if (t.StartsWith("|")) t = t[1..];
        if (t.EndsWith("|")) t = t[..^1];

        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inCode = false;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|') { sb.Append('|'); i++; continue; }
            if (c == '`') inCode = !inCode;
            if (c == '|' && !inCode) { cells.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(c);
        }
        cells.Add(sb.ToString().Trim());
        return cells;
    }

    /// <summary>Very small inline parser: `code`, **bold**, *italic*, [text](url).</summary>
    public static List<Run> ParseInline(string s)
    {
        var runs = new List<Run>();
        var buf = new System.Text.StringBuilder();

        void Emit(bool bold, bool italic, bool code, bool link)
        {
            if (buf.Length == 0) return;
            runs.Add(new Run { Text = buf.ToString(), Bold = bold, Italic = italic, Code = code, Link = link });
            buf.Clear();
        }

        int i = 0;
        bool bold = false, italic = false, code = false;
        while (i < s.Length)
        {
            char c = s[i];

            if (c == '\\' && i + 1 < s.Length)
            {
                Emit(bold, italic, code, false);
                buf.Append(s[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int end = s.IndexOf('`', i + 1);
                if (end > i)
                {
                    Emit(bold, italic, code, false);
                    runs.Add(new Run { Text = s[(i + 1)..end], Code = true, Bold = bold, Italic = italic });
                    i = end + 1;
                    continue;
                }
            }

            if (Doubled(s, i, '*') || Doubled(s, i, '_'))
            {
                int end = s.IndexOf(s.Substring(i, 2), i + 2);
                if (end > i)
                {
                    Emit(bold, italic, code, false);
                    string inner = s[(i + 2)..end];
                    runs.AddRange(ParseInline(inner).Select(r => new Run { Text = r.Text, Bold = true, Italic = r.Italic, Code = r.Code, Link = r.Link }));
                    i = end + 2;
                    continue;
                }
            }

            if ((c == '*' || c == '_') && !Doubled(s, i, c))
            {
                int end = s.IndexOf(c, i + 1);
                if (end > i + 1)
                {
                    Emit(bold, italic, code, false);
                    string inner = s[(i + 1)..end];
                    runs.AddRange(ParseInline(inner).Select(r => new Run { Text = r.Text, Bold = r.Bold, Italic = true, Code = r.Code, Link = r.Link }));
                    i = end + 1;
                    continue;
                }
            }

            if (c == '[')
            {
                int close = s.IndexOf(']', i + 1);
                if (close > i && close + 1 < s.Length && s[close + 1] == '(')
                {
                    int urlEnd = s.IndexOf(')', close + 2);
                    if (urlEnd > close)
                    {
                        Emit(bold, italic, code, false);
                        runs.Add(new Run { Text = s[(i + 1)..close], Link = true, Bold = bold, Italic = italic });
                        i = urlEnd + 1;
                        continue;
                    }
                }
            }

            buf.Append(c);
            i++;
        }
        Emit(bold, italic, code, false);
        return runs;
    }

    private static bool Doubled(string s, int i, char c) => i + 1 < s.Length && s[i] == c && s[i + 1] == c;
}
