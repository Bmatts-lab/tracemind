using System.Globalization;
using TraceMind.Core.Geometry;

namespace TraceMind.Core.Export;

/// <summary>Minimal single-stroke vector font for silkscreen text (glyphs on a 4×6 grid).</summary>
public static class StrokeFont
{
    static readonly Dictionary<char, string> Glyphs = new()
    {
        ['0'] = "0,0 4,0 4,6 0,6 0,0;0,6 4,0", ['1'] = "1,1 2,0 2,6;1,6 3,6", ['2'] = "0,1 1,0 3,0 4,1 4,2 0,6 4,6",
        ['3'] = "0,0 4,0 2,2 3,2 4,3 4,5 3,6 1,6 0,5", ['4'] = "3,6 3,0 0,4 4,4", ['5'] = "4,0 0,0 0,2 3,2 4,3 4,5 3,6 0,6",
        ['6'] = "4,0 1,0 0,1 0,5 1,6 3,6 4,5 4,4 3,3 0,3", ['7'] = "0,0 4,0 1,6",
        ['8'] = "1,0 3,0 4,1 4,2 3,3 1,3 0,4 0,5 1,6 3,6 4,5 4,4 3,3;1,3 0,2 0,1 1,0", ['9'] = "4,3 1,3 0,2 0,1 1,0 3,0 4,1 4,5 3,6 0,6",
        ['A'] = "0,6 0,2 2,0 4,2 4,6;0,3 4,3", ['B'] = "0,0 0,6 3,6 4,5 4,4 3,3 0,3;0,0 3,0 4,1 4,2 3,3", ['C'] = "4,0 1,0 0,1 0,5 1,6 4,6",
        ['D'] = "0,0 0,6 2,6 4,4 4,2 2,0 0,0", ['E'] = "4,0 0,0 0,6 4,6;0,3 3,3", ['F'] = "4,0 0,0 0,6;0,3 3,3",
        ['G'] = "4,1 3,0 1,0 0,1 0,5 1,6 3,6 4,5 4,3 2,3", ['H'] = "0,0 0,6;4,0 4,6;0,3 4,3", ['I'] = "1,0 3,0;2,0 2,6;1,6 3,6",
        ['J'] = "4,0 4,5 3,6 1,6 0,5", ['K'] = "0,0 0,6;4,0 0,4;1,3 4,6", ['L'] = "0,0 0,6 4,6", ['M'] = "0,6 0,0 2,3 4,0 4,6",
        ['N'] = "0,6 0,0 4,6 4,0", ['O'] = "1,0 3,0 4,1 4,5 3,6 1,6 0,5 0,1 1,0", ['P'] = "0,6 0,0 3,0 4,1 4,2 3,3 0,3",
        ['Q'] = "1,0 3,0 4,1 4,5 3,6 1,6 0,5 0,1 1,0;2,4 4,6", ['R'] = "0,6 0,0 3,0 4,1 4,2 3,3 0,3;2,3 4,6",
        ['S'] = "4,1 3,0 1,0 0,1 0,2 1,3 3,3 4,4 4,5 3,6 1,6 0,5", ['T'] = "0,0 4,0;2,0 2,6", ['U'] = "0,0 0,5 1,6 3,6 4,5 4,0",
        ['V'] = "0,0 2,6 4,0", ['W'] = "0,0 1,6 2,3 3,6 4,0", ['X'] = "0,0 4,6;4,0 0,6", ['Y'] = "0,0 2,3 4,0;2,3 2,6", ['Z'] = "0,0 4,0 0,6 4,6",
        ['+'] = "0,3 4,3;2,1 2,5", ['-'] = "0,3 4,3", ['.'] = "2,5.5 2,6", ['_'] = "0,6 4,6", ['/'] = "4,0 0,6", ['\\'] = "0,0 4,6",
        ['('] = "3,0 2,1 2,5 3,6", [')'] = "1,0 2,1 2,5 1,6", [':'] = "2,1.5 2,2;2,4.5 2,5", ['#'] = "1,0 1,6;3,0 3,6;0,2 4,2;0,4 4,4",
        ['='] = "0,2 4,2;0,4 4,4", ['%'] = "0,6 4,0;0,0 1,0 1,1 0,1 0,0;3,5 4,5 4,6 3,6 3,5", ['?'] = "0,1 1,0 3,0 4,1 4,2 2,3 2,4;2,5.5 2,6",
        ['!'] = "2,0 2,4;2,5.5 2,6", [','] = "2,5 1.5,6.5", ['*'] = "2,1 2,5;0,2 4,4;4,2 0,4", ['~'] = "0,3 1,2 3,4 4,3",
        ['&'] = "4,6 1,2 1,1 2,0 3,1 3,2 0,4 0,5 1,6 2,6 4,4", ['\''] = "2,0 2,1.5", ['"'] = "1,0 1,1.5;3,0 3,1.5",
        ['<'] = "4,1 0,3 4,5", ['>'] = "0,1 4,3 0,5", ['['] = "3,0 2,0 2,6 3,6", [']'] = "1,0 2,0 2,6 1,6", ['@'] = "3,4 3,2 1,2 1,4 3,4 4,4 4,1 3,0 1,0 0,1 0,5 1,6 4,6",
    };

    static readonly Dictionary<char, List<List<Vec2>>> Parsed = Glyphs.ToDictionary(kv => kv.Key, kv => Parse(kv.Value));

    static List<List<Vec2>> Parse(string s)
    {
        var strokes = new List<List<Vec2>>();
        foreach (var poly in s.Split(';'))
        {
            var pts = new List<Vec2>();
            foreach (var pair in poly.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = pair.Split(',');
                pts.Add(new(double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture)));
            }
            strokes.Add(pts);
        }
        return strokes;
    }

    public static double TextWidth(string text, double height) => text.Length * height;

    /// <summary>Strokes (polylines) for text centred on <paramref name="center"/>, rotated by degrees, optionally mirrored (bottom side).</summary>
    public static List<List<Vec2>> Layout(string text, Vec2 center, double height, double rotation = 0, bool mirror = false)
    {
        var result = new List<List<Vec2>>();
        double u = height / 6.0, adv = 6 * u, totalW = text.Length * adv - 2 * u;
        double x0 = -totalW / 2, y0 = -height / 2;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = char.ToUpperInvariant(text[i]);
            if (ch == 'µ') ch = 'U';
            if (ch == 'Ω') ch = 'O';
            if (!Parsed.TryGetValue(ch, out var strokes)) continue;
            foreach (var s in strokes)
            {
                var poly = new List<Vec2>(s.Count);
                foreach (var p in s)
                {
                    var local = new Vec2(x0 + i * adv + p.X * u, y0 + p.Y * u);
                    if (mirror) local = new(-local.X, local.Y);
                    poly.Add(center + local.Rotate(rotation));
                }
                result.Add(poly);
            }
        }
        return result;
    }
}
