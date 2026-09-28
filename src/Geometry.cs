using System;
using System.Collections.Generic;

namespace ConnectedMining
{
    // Independent of Unity so the narrow-phase geometry can be exercised outside the game.
    internal readonly struct Vec
    {
        public readonly double X, Y, Z;
        public Vec(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static Vec operator +(Vec a, Vec b) => new Vec(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec operator -(Vec a, Vec b) => new Vec(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec operator *(Vec a, double s) => new Vec(a.X * s, a.Y * s, a.Z * s);
        public static double Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec Cross(Vec a, Vec b) => new Vec(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public double LengthSquared => Dot(this, this);
    }

    internal readonly struct Triangle
    {
        public readonly Vec A, B, C, Min, Max;
        public Triangle(Vec a, Vec b, Vec c)
        {
            A = a; B = b; C = c;
            Min = new Vec(Math.Min(a.X, Math.Min(b.X, c.X)), Math.Min(a.Y, Math.Min(b.Y, c.Y)), Math.Min(a.Z, Math.Min(b.Z, c.Z)));
            Max = new Vec(Math.Max(a.X, Math.Max(b.X, c.X)), Math.Max(a.Y, Math.Max(b.Y, c.Y)), Math.Max(a.Z, Math.Max(b.Z, c.Z)));
        }
    }

    internal static class Geometry
    {
        private const double Epsilon = 1e-12;
        private static double Clamp(double x) => Math.Max(0, Math.Min(1, x));

        internal static bool Touches(Triangle[] a, Triangle[] b, double tolerance)
        {
            double squared = tolerance * tolerance;
            foreach (var x in a)
                foreach (var y in b)
                {
                    if (x.Min.X > y.Max.X + tolerance || y.Min.X > x.Max.X + tolerance ||
                        x.Min.Y > y.Max.Y + tolerance || y.Min.Y > x.Max.Y + tolerance ||
                        x.Min.Z > y.Max.Z + tolerance || y.Min.Z > x.Max.Z + tolerance) continue;
                    if (DistanceSquared(x, y) <= squared + Epsilon) return true;
                }
            // Surface distances alone miss a small closed collider entirely enclosed by a larger one.
            return a.Length > 0 && b.Length > 0 && (Inside(a[0].A, b) || Inside(b[0].A, a));
        }

        private static bool Inside(Vec point, Triangle[] mesh)
        {
            var distances = new List<double>();
            var direction = new Vec(1, 0.371390676354, 0.694746590607);
            foreach (var triangle in mesh)
                if (Ray(point, direction, triangle, out double t) && t > 1e-7)
                {
                    bool duplicate = false;
                    foreach (double previous in distances)
                        if (Math.Abs(previous - t) < 1e-6) { duplicate = true; break; }
                    if (!duplicate) distances.Add(t);
                }
            return distances.Count % 2 == 1;
        }

        private static bool Ray(Vec origin, Vec direction, Triangle t, out double distance)
        {
            distance = 0;
            Vec edge1 = t.B - t.A, edge2 = t.C - t.A;
            Vec p = Vec.Cross(direction, edge2);
            double determinant = Vec.Dot(edge1, p);
            if (Math.Abs(determinant) < Epsilon) return false;
            double inverse = 1 / determinant;
            Vec s = origin - t.A;
            double u = Vec.Dot(s, p) * inverse;
            if (u < -Epsilon || u > 1 + Epsilon) return false;
            Vec q = Vec.Cross(s, edge1);
            double v = Vec.Dot(direction, q) * inverse;
            if (v < -Epsilon || u + v > 1 + Epsilon) return false;
            distance = Vec.Dot(edge2, q) * inverse;
            return distance >= -Epsilon;
        }

        private static bool Intersects(Vec a, Vec b, Triangle t) => Ray(a, b - a, t, out double s) && s <= 1 + Epsilon;

        internal static double DistanceSquared(Triangle a, Triangle b)
        {
            if (Intersects(a.A, a.B, b) || Intersects(a.B, a.C, b) || Intersects(a.C, a.A, b) ||
                Intersects(b.A, b.B, a) || Intersects(b.B, b.C, a) || Intersects(b.C, b.A, a)) return 0;
            double best = Math.Min(PointTriangle(a.A, b), Math.Min(PointTriangle(a.B, b), PointTriangle(a.C, b)));
            best = Math.Min(best, Math.Min(PointTriangle(b.A, a), Math.Min(PointTriangle(b.B, a), PointTriangle(b.C, a))));
            best = Math.Min(best, EdgeAgainstTriangle(a.A, a.B, b));
            best = Math.Min(best, EdgeAgainstTriangle(a.B, a.C, b));
            return Math.Min(best, EdgeAgainstTriangle(a.C, a.A, b));
        }

        private static double EdgeAgainstTriangle(Vec a, Vec b, Triangle t) =>
            Math.Min(SegmentDistance(a, b, t.A, t.B), Math.Min(SegmentDistance(a, b, t.B, t.C), SegmentDistance(a, b, t.C, t.A)));

        private static double PointSegment(Vec p, Vec a, Vec b)
        {
            Vec edge = b - a;
            double s = edge.LengthSquared < Epsilon ? 0 : Clamp(Vec.Dot(p - a, edge) / edge.LengthSquared);
            return (p - (a + edge * s)).LengthSquared;
        }

        private static double PointTriangle(Vec p, Triangle t)
        {
            Vec ab = t.B - t.A, ac = t.C - t.A, normal = Vec.Cross(ab, ac);
            if (normal.LengthSquared < Epsilon)
                return Math.Min(PointSegment(p, t.A, t.B), Math.Min(PointSegment(p, t.B, t.C), PointSegment(p, t.C, t.A)));
            Vec ap = p - t.A;
            double d1 = Vec.Dot(ab, ap), d2 = Vec.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return ap.LengthSquared;
            Vec bp = p - t.B;
            double d3 = Vec.Dot(ab, bp), d4 = Vec.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return bp.LengthSquared;
            double vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return (p - (t.A + ab * (d1 / (d1 - d3)))).LengthSquared;
            Vec cp = p - t.C;
            double d5 = Vec.Dot(ab, cp), d6 = Vec.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return cp.LengthSquared;
            double vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return (p - (t.A + ac * (d2 / (d2 - d6)))).LengthSquared;
            double va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
                return (p - (t.B + (t.C - t.B) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).LengthSquared;
            double n = Vec.Dot(ap, normal);
            return n * n / normal.LengthSquared;
        }

        private static double SegmentDistance(Vec p, Vec q, Vec r, Vec s)
        {
            Vec d1 = q - p, d2 = s - r, relative = p - r;
            double a = d1.LengthSquared, e = d2.LengthSquared, f = Vec.Dot(d2, relative), u, v;
            if (a <= Epsilon && e <= Epsilon) return relative.LengthSquared;
            if (a <= Epsilon) { u = 0; v = Clamp(f / e); }
            else
            {
                double c = Vec.Dot(d1, relative);
                if (e <= Epsilon) { v = 0; u = Clamp(-c / a); }
                else
                {
                    double b = Vec.Dot(d1, d2), denominator = a * e - b * b;
                    u = denominator > Epsilon ? Clamp((b * f - c * e) / denominator) : 0;
                    v = (b * u + f) / e;
                    if (v < 0) { v = 0; u = Clamp(-c / a); }
                    else if (v > 1) { v = 1; u = Clamp((b - c) / a); }
                }
            }
            return (p + d1 * u - r - d2 * v).LengthSquared;
        }
    }
}
