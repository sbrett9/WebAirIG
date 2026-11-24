namespace WebAirIG.Common.Simulation;

/// <summary>2D point in pattern coordinates (x = east-ish, y = north-ish), meters.</summary>
public readonly record struct P2(double X, double Y)
{
    public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Y + b.Y);
    public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);
    public static P2 operator *(P2 a, double s) => new(a.X * s, a.Y * s);
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>
/// A closed 2D path resampled by arc length. Heading is clockwise from +Y (north); curvature is signed
/// (positive = turning right / clockwise), in 1/m.
/// </summary>
public sealed class SampledLoop
{
    private readonly P2[] _p;
    private readonly double[] _s;
    private readonly double[] _heading;   // unwrapped, radians
    private readonly double[] _curvature; // 1/m

    public double Length { get; }

    private SampledLoop(P2[] p, double[] s, double[] heading, double[] curvature, double length)
    {
        _p = p; _s = s; _heading = heading; _curvature = curvature; Length = length;
    }

    /// <summary>Builds from a dense list of points on a closed path (last point should not duplicate the first).</summary>
    public static SampledLoop FromPoints(IReadOnlyList<P2> points, int smoothingWindow = 6)
    {
        var pts = points.ToList();
        if (pts.Count > 1 && (pts[^1] - pts[0]).Length < 1e-6) pts.RemoveAt(pts.Count - 1);
        int n = pts.Count;
        if (n < 3) throw new ArgumentException("A loop needs at least 3 points");

        var p = new P2[n + 1];
        var s = new double[n + 1];
        for (int i = 0; i < n; i++) p[i] = pts[i];
        p[n] = pts[0];
        for (int i = 1; i <= n; i++) s[i] = s[i - 1] + (p[i] - p[i - 1]).Length;
        double length = s[n];

        // Heading at each vertex from central differences, unwrapped.
        var heading = new double[n + 1];
        for (int i = 0; i <= n; i++)
        {
            var a = p[(i - 1 + n) % n];
            var b = p[(i + 1) % n];
            var d = b - a;
            heading[i] = Math.Atan2(d.X, d.Y);
        }
        for (int i = 1; i <= n; i++)
        {
            double dh = heading[i] - heading[i - 1];
            heading[i] -= 2 * Math.PI * Math.Round(dh / (2 * Math.PI));
        }

        // Curvature = dHeading/ds, smoothed with a centered moving window (cyclic).
        var raw = new double[n];
        for (int i = 0; i < n; i++)
        {
            int i0 = (i - 1 + n) % n, i1 = (i + 1) % n;
            double dh = WrapPi(Math.Atan2((p[i1] - p[i]).X, (p[i1] - p[i]).Y) - Math.Atan2((p[i] - p[i0]).X, (p[i] - p[i0]).Y));
            double ds = 0.5 * ((p[i1] - p[i]).Length + (p[i] - p[i0]).Length);
            raw[i] = ds > 1e-9 ? dh / ds : 0;
        }
        var curvature = new double[n + 1];
        for (int i = 0; i < n; i++)
        {
            double acc = 0;
            for (int k = -smoothingWindow; k <= smoothingWindow; k++) acc += raw[((i + k) % n + n) % n];
            curvature[i] = acc / (2 * smoothingWindow + 1);
        }
        curvature[n] = curvature[0];
        return new SampledLoop(p, s, heading, curvature, length);
    }

    /// <summary>Samples a closed parametric curve f(u), u in [0,1).</summary>
    public static SampledLoop FromFunction(Func<double, P2> f, int samples = 4096)
    {
        var pts = new List<P2>(samples);
        for (int i = 0; i < samples; i++) pts.Add(f((double)i / samples));
        return FromPoints(pts);
    }

    public (P2 Position, double HeadingRad, double Curvature) Evaluate(double s)
    {
        if (Length <= 0) return (_p[0], _heading[0], 0);
        s %= Length;
        if (s < 0) s += Length;
        int i = Array.BinarySearch(_s, s);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, _s.Length - 2);
        double seg = _s[i + 1] - _s[i];
        double t = seg > 1e-12 ? (s - _s[i]) / seg : 0;
        var pos = _p[i] + (_p[i + 1] - _p[i]) * t;
        double h = _heading[i] + (_heading[i + 1] - _heading[i]) * t;
        double k = _curvature[i] + (_curvature[i + 1] - _curvature[i]) * t;
        return (pos, h, k);
    }

    public static double WrapPi(double a)
    {
        a %= 2 * Math.PI;
        if (a > Math.PI) a -= 2 * Math.PI;
        if (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }
}

/// <summary>Builds closed paths from straight segments and constant-radius arcs ("turtle" style).</summary>
public sealed class PathBuilder
{
    private readonly List<P2> _points = [];
    private readonly double _step;
    private P2 _pos;
    private double _heading; // radians, clockwise from north

    public PathBuilder(P2 start, double headingDeg, double stepM = 5)
    {
        _pos = start;
        _heading = headingDeg * Math.PI / 180;
        _step = stepM;
        _points.Add(start);
    }

    public PathBuilder Straight(double length)
    {
        int n = Math.Max(1, (int)Math.Ceiling(length / _step));
        var dir = new P2(Math.Sin(_heading), Math.Cos(_heading));
        var start = _pos;
        for (int i = 1; i <= n; i++) _points.Add(start + dir * (length * i / n));
        _pos = start + dir * length;
        return this;
    }

    /// <summary>Arc of the given radius; positive angle = right (clockwise) turn.</summary>
    public PathBuilder Arc(double radius, double angleDeg)
    {
        double sign = Math.Sign(angleDeg);
        double total = Math.Abs(angleDeg) * Math.PI / 180;
        var right = new P2(Math.Cos(_heading), -Math.Sin(_heading));
        var center = _pos + right * (radius * sign);
        int n = Math.Max(2, (int)Math.Ceiling(radius * total / _step));
        for (int i = 1; i <= n; i++)
        {
            double h = _heading + sign * total * i / n;
            var r = new P2(Math.Cos(h), -Math.Sin(h));
            _points.Add(center - r * (radius * sign));
        }
        _heading += sign * total;
        _pos = _points[^1];
        return this;
    }

    public IReadOnlyList<P2> Points => _points;
}
