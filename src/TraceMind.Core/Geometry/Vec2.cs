using System.Text.Json.Serialization;

namespace TraceMind.Core.Geometry;

/// <summary>2D point/vector in millimetres. Y grows downwards (screen convention, same as KiCad).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    [JsonIgnore] public double Length => Math.Sqrt(X * X + Y * Y);
    [JsonIgnore] public double LengthSq => X * X + Y * Y;
    public double Dot(Vec2 o) => X * o.X + Y * o.Y;
    public double Cross(Vec2 o) => X * o.Y - Y * o.X;
    public double DistanceTo(Vec2 o) => (this - o).Length;
    public Vec2 Normalized() { var l = Length; return l < 1e-12 ? Zero : this / l; }

    /// <summary>Rotate by degrees (positive = counter-clockwise on screen, i.e. KiCad convention with Y down).</summary>
    public Vec2 Rotate(double degrees)
    {
        if (degrees == 0) return this;
        var norm = ((degrees % 360) + 360) % 360;
        if (norm == 90) return new(Y, -X);
        if (norm == 180) return new(-X, -Y);
        if (norm == 270) return new(-Y, X);
        var r = -degrees * Math.PI / 180.0;
        var c = Math.Cos(r); var s = Math.Sin(r);
        return new(X * c - Y * s, X * s + Y * c);
    }

    public Vec2 Snap(double grid) => grid <= 0 ? this : new(Math.Round(X / grid) * grid, Math.Round(Y / grid) * grid);
    public bool ApproxEquals(Vec2 o, double eps = 1e-4) => Math.Abs(X - o.X) < eps && Math.Abs(Y - o.Y) < eps;
    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

public readonly record struct Rect(double MinX, double MinY, double MaxX, double MaxY)
{
    public static readonly Rect Empty = new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
    [JsonIgnore] public bool IsEmpty => MinX > MaxX || MinY > MaxY;
    [JsonIgnore] public double Width => MaxX - MinX;
    [JsonIgnore] public double Height => MaxY - MinY;
    [JsonIgnore] public Vec2 Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);
    public static Rect FromPoints(Vec2 a, Vec2 b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    public static Rect FromCenter(Vec2 c, double w, double h) => new(c.X - w / 2, c.Y - h / 2, c.X + w / 2, c.Y + h / 2);
    public Rect Include(Vec2 p) => new(Math.Min(MinX, p.X), Math.Min(MinY, p.Y), Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));
    public Rect Union(Rect o) => o.IsEmpty ? this : IsEmpty ? o : new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));
    public Rect Inflate(double d) => new(MinX - d, MinY - d, MaxX + d, MaxY + d);
    public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
    public bool Intersects(Rect o) => !(o.MinX > MaxX || o.MaxX < MinX || o.MinY > MaxY || o.MaxY < MinY);
}
