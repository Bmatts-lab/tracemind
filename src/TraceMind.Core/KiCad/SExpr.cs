using System.Globalization;
using System.Text;

namespace TraceMind.Core.KiCad;

/// <summary>S-expression node as used by KiCad files.</summary>
public sealed class SNode
{
    public string? Atom { get; }
    public List<SNode>? Children { get; }
    public bool Quoted { get; }

    public SNode(string atom, bool quoted = false) { Atom = atom; Quoted = quoted; }
    public SNode(params SNode[] children) { Children = [.. children]; }
    public SNode(IEnumerable<SNode> children) { Children = [.. children]; }

    public bool IsList => Children != null;
    public string Head => Children is { Count: > 0 } c && c[0].Atom != null ? c[0].Atom! : "";
    public SNode this[int i] => Children![i];
    public int Count => Children?.Count ?? 0;

    public IEnumerable<SNode> All(string head) => Children?.Where(c => c.IsList && c.Head == head) ?? [];
    public SNode? First(string head) => Children?.FirstOrDefault(c => c.IsList && c.Head == head);
    public string? Str(int i) => Children != null && i < Children.Count ? Children[i].Atom : null;
    public double Num(int i, double def = 0) => double.TryParse(Str(i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
    /// <summary>Value of a child list like (key value).</summary>
    public string? Get(string head, int i = 1) => First(head)?.Str(i);

    // -------- building helpers --------
    public static SNode L(string head, params object?[] items)
    {
        var list = new List<SNode> { new(head) };
        foreach (var it in items)
        {
            switch (it)
            {
                case null: break;
                case SNode n: list.Add(n); break;
                case IEnumerable<SNode> many: list.AddRange(many); break;
                case string s: list.Add(new SNode(s, true)); break;
                case double d: list.AddRange([new SNode(Fmt(d))]); break;
                case int i: list.Add(new SNode(i.ToString(CultureInfo.InvariantCulture))); break;
                case bool b: list.Add(new SNode(b ? "yes" : "no")); break;
                default: list.Add(new SNode(Convert.ToString(it, CultureInfo.InvariantCulture)!)); break;
            }
        }
        return new SNode(list);
    }
    public static SNode Sym(string s) => new(s, false);
    public static string Fmt(double d) => Math.Round(d, 6).ToString("0.######", CultureInfo.InvariantCulture);

    // -------- parse --------
    public static SNode Parse(string text)
    {
        int i = 0;
        SkipWs(text, ref i);
        var node = ParseNode(text, ref i);
        return node;
    }

    static void SkipWs(string t, ref int i) { while (i < t.Length && char.IsWhiteSpace(t[i])) i++; }

    static SNode ParseNode(string t, ref int i)
    {
        SkipWs(t, ref i);
        if (i >= t.Length) throw new FormatException("Unexpected end of file");
        if (t[i] == '(')
        {
            i++;
            var list = new List<SNode>();
            while (true)
            {
                SkipWs(t, ref i);
                if (i >= t.Length) throw new FormatException("Unclosed list");
                if (t[i] == ')') { i++; break; }
                list.Add(ParseNode(t, ref i));
            }
            return new SNode(list);
        }
        if (t[i] == '"')
        {
            i++;
            var sb = new StringBuilder();
            while (i < t.Length && t[i] != '"')
            {
                if (t[i] == '\\' && i + 1 < t.Length)
                {
                    i++;
                    sb.Append(t[i] switch { 'n' => '\n', 't' => '\t', _ => t[i] });
                }
                else sb.Append(t[i]);
                i++;
            }
            i++;
            return new SNode(sb.ToString(), true);
        }
        int start = i;
        while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != '(' && t[i] != ')') i++;
        return new SNode(t[start..i]);
    }

    // -------- write --------
    public override string ToString()
    {
        var sb = new StringBuilder();
        Write(sb, 0);
        return sb.ToString();
    }

    void Write(StringBuilder sb, int indent)
    {
        if (!IsList)
        {
            if (Quoted || Atom!.Length == 0 || Atom.Any(c => char.IsWhiteSpace(c) || c == '(' || c == ')' || c == '"'))
                sb.Append('"').Append(Atom!.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")).Append('"');
            else sb.Append(Atom);
            return;
        }
        bool simple = Children!.All(c => !c.IsList) || (Children!.Count <= 4 && Children.All(c => !c.IsList || c.Children!.All(cc => !cc.IsList)));
        sb.Append('(');
        for (int k = 0; k < Children!.Count; k++)
        {
            var c = Children[k];
            if (k > 0)
            {
                if (!simple && c.IsList) { sb.Append('\n').Append(' ', (indent + 1) * 2); }
                else sb.Append(' ');
            }
            c.Write(sb, indent + 1);
        }
        if (!simple) sb.Append('\n').Append(' ', indent * 2);
        sb.Append(')');
    }
}
