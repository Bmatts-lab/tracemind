namespace TraceMind.Core.Geometry;

/// <summary>
/// A convex copper shape: a "core" (point, segment or convex polygon) inflated by a radius.
/// Covers round pads (point+r), tracks/ovals (segment+r) and rectangular pads (polygon+0).
/// </summary>
public sealed class Shape
{
    public Vec2[] Core { get; }
    public double Radius { get; }
    public Rect Bounds { get; }

    private Shape(Vec2[] core, double radius)
    {
        Core = core; Radius = radius;
        var b = Rect.Empty;
        foreach (var p in core) b = b.Include(p);
        Bounds = b.Inflate(radius);
    }

    public static Shape Circle(Vec2 c, double r) => new([c], r);
    public static Shape Capsule(Vec2 a, Vec2 b, double r) => a.ApproxEquals(b, 1e-9) ? new([a], r) : new([a, b], r);
    public static Shape Polygon(Vec2[] pts, double r = 0) => new(pts, r);

    /// <summary>Rectangle centred at c, size w×h, rotated by deg; optional corner radius.</summary>
    public static Shape Box(Vec2 c, double w, double h, double deg, double cornerRadius = 0)
    {
        cornerRadius = Math.Min(cornerRadius, Math.Min(w, h) / 2);
        var hw = w / 2 - cornerRadius; var hh = h / 2 - cornerRadius;
        if (hw <= 1e-9 && hh <= 1e-9) return Circle(c, cornerRadius);
        if (hw <= 1e-9) return Capsule(c + new Vec2(0, -hh).Rotate(deg), c + new Vec2(0, hh).Rotate(deg), cornerRadius);
        if (hh <= 1e-9) return Capsule(c + new Vec2(-hw, 0).Rotate(deg), c + new Vec2(hw, 0).Rotate(deg), cornerRadius);
        return new([
            c + new Vec2(-hw, -hh).Rotate(deg), c + new Vec2(hw, -hh).Rotate(deg),
            c + new Vec2(hw, hh).Rotate(deg), c + new Vec2(-hw, hh).Rotate(deg)], cornerRadius);
    }

    public Shape Inflate(double d) => new(Core, Radius + d);

    /// <summary>Edge distance between two shapes (negative = overlap).</summary>
    public static double Distance(Shape a, Shape b) => CoreDistance(a.Core, b.Core) - a.Radius - b.Radius;

    public double DistanceTo(Vec2 p) => CoreDistance(Core, [p]) - Radius;
    public bool Contains(Vec2 p) => DistanceTo(p) <= 0;

    public static double CoreDistance(Vec2[] a, Vec2[] b)
    {
        if (a.Length >= 3 && b.Length >= 1 && PointInConvex(a, b[0])) return 0;
        if (b.Length >= 3 && a.Length >= 1 && PointInConvex(b, a[0])) return 0;
        double best = double.MaxValue;
        int na = a.Length == 1 ? 1 : (a.Length == 2 ? 1 : a.Length);
        int nb = b.Length == 1 ? 1 : (b.Length == 2 ? 1 : b.Length);
        for (int i = 0; i < na; i++)
        {
            var a0 = a[i]; var a1 = a.Length == 1 ? a[0] : a[(i + 1) % a.Length];
            for (int j = 0; j < nb; j++)
            {
                var b0 = b[j]; var b1 = b.Length == 1 ? b[0] : b[(j + 1) % b.Length];
                var d = SegSegDistance(a0, a1, b0, b1);
                if (d < best) best = d;
                if (best <= 0) return 0;
            }
        }
        return best;
    }

    public static bool PointInConvex(Vec2[] poly, Vec2 p)
    {
        int sign = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Length];
            var c = (b - a).Cross(p - a);
            int s = c > 1e-12 ? 1 : c < -1e-12 ? -1 : 0;
            if (s == 0) continue;
            if (sign == 0) sign = s; else if (s != sign) return false;
        }
        return true;
    }

    public static double PointSegDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a; var l2 = ab.LengthSq;
        if (l2 < 1e-18) return p.DistanceTo(a);
        var t = Math.Clamp((p - a).Dot(ab) / l2, 0, 1);
        return p.DistanceTo(a + ab * t);
    }

    public static Vec2 ClosestPointOnSeg(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a; var l2 = ab.LengthSq;
        if (l2 < 1e-18) return a;
        var t = Math.Clamp((p - a).Dot(ab) / l2, 0, 1);
        return a + ab * t;
    }

    public static bool SegmentsIntersect(Vec2 p1, Vec2 p2, Vec2 q1, Vec2 q2)
    {
        double d1 = (q2 - q1).Cross(p1 - q1), d2 = (q2 - q1).Cross(p2 - q1);
        double d3 = (p2 - p1).Cross(q1 - p1), d4 = (p2 - p1).Cross(q2 - p1);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    public static double SegSegDistance(Vec2 a0, Vec2 a1, Vec2 b0, Vec2 b1)
    {
        if (SegmentsIntersect(a0, a1, b0, b1)) return 0;
        return Math.Min(Math.Min(PointSegDistance(a0, b0, b1), PointSegDistance(a1, b0, b1)),
                        Math.Min(PointSegDistance(b0, a0, a1), PointSegDistance(b1, a0, a1)));
    }

    /// <summary>Approximate outline as a polygon (for rendering / gerber regions).</summary>
    public List<Vec2> ToOutline(int arcSegments = 8)
    {
        var pts = new List<Vec2>();
        if (Core.Length == 1)
        {
            int n = arcSegments * 4;
            for (int i = 0; i < n; i++) { var a = 2 * Math.PI * i / n; pts.Add(Core[0] + new Vec2(Math.Cos(a), Math.Sin(a)) * Radius); }
            return pts;
        }
        if (Radius <= 1e-9) return [.. Core];
        // Rounded convex polygon (also works for a segment = 2-gon).
        var center = Vec2.Zero; foreach (var c in Core) center += c; center /= Core.Length;
        for (int i = 0; i < Core.Length; i++)
        {
            var prev = Core[(i - 1 + Core.Length) % Core.Length]; var cur = Core[i]; var next = Core[(i + 1) % Core.Length];
            var n1 = Normal(prev, cur, center); var n2 = Normal(cur, next, center);
            double a1 = Math.Atan2(n1.Y, n1.X), a2 = Math.Atan2(n2.Y, n2.X);
            // sweep from a1 to a2 in the direction going around the outside
            double sweep = a2 - a1;
            var cross = n1.Cross(n2);
            if (cross >= 0) { while (sweep < 0) sweep += 2 * Math.PI; } else { while (sweep > 0) sweep -= 2 * Math.PI; }
            if (Core.Length == 2 && Math.Abs(sweep) < 1e-6) sweep = Math.PI;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) * arcSegments));
            for (int s = 0; s <= steps; s++) { var a = a1 + sweep * s / steps; pts.Add(cur + new Vec2(Math.Cos(a), Math.Sin(a)) * Radius); }
        }
        return pts;
    }

    private static Vec2 Normal(Vec2 a, Vec2 b, Vec2 center)
    {
        var d = (b - a).Normalized();
        var n = new Vec2(d.Y, -d.X);
        var mid = (a + b) / 2;
        if ((mid - center).Dot(n) < 0) n = -n;
        if ((mid - center).LengthSq < 1e-18) n = new Vec2(d.Y, -d.X);
        return n;
    }
}
