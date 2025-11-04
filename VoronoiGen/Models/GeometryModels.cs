using System.Collections.Generic;
using System.Numerics;

namespace VoronoiGen.Models
{
    // Simple double-precision rectangle used for bounds (cross-platform; no System.Drawing/Skia).
    public readonly record struct RectD(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public static RectD Empty => new(0, 0, 0, 0);
        public override string ToString() => $"L={Left},T={Top},W={Width},H={Height}";
    }

    // Core polygon model used across services and rendering
    public record Polygon(List<Vector2> Points)
    {
        // Compute axis-aligned bounding box — used for viewBox and random sampling bounds
        public RectD GetBounds()
        {
            if (Points.Count == 0) return RectD.Empty;
            double minX = Points[0].X, maxX = Points[0].X, minY = Points[0].Y, maxY = Points[0].Y;
            foreach (var p in Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return new RectD(minX, minY, maxX - minX, maxY - minY);
        }

        // Signed area (positive for CCW) — used to pick largest outer boundary
        public double SignedArea()
        {
            double a = 0;
            for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
            {
                a += (double)(Points[j].X * Points[i].Y - Points[i].X * Points[j].Y);
            }
            return 0.5 * a;
        }

        public double Area() => System.Math.Abs(SignedArea());

        // Ensure polygon is oriented CCW — helpful for consistent clipping and centroid calc
        public Polygon EnsureCcw()
        {
            if (SignedArea() < 0)
            {
                var reversed = new List<Vector2>(Points);
                reversed.Reverse();
                return new Polygon(reversed);
            }
            return this;
        }

        // Geometric centroid of a (potentially closed) polygon using the shoelace formula.
        // Returns the arithmetic mean when area is ~0 to avoid NaN; used by Lloyd relaxation.
        public Vector2 Centroid()
        {
            if (Points.Count == 0) return default;
            // Support rings that repeat the first point at the end by iterating with wrap-around
            double cx = 0, cy = 0, a = 0;
            for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
            {
                double xi = Points[i].X;
                double yi = Points[i].Y;
                double xj = Points[j].X;
                double yj = Points[j].Y;
                double cross = xj * yi - xi * yj;
                a += cross;
                cx += (xj + xi) * cross;
                cy += (yj + yi) * cross;
            }
            a *= 0.5;
            if (System.Math.Abs(a) < 1e-9)
            {
                // Degenerate polygon: fall back to average of vertices
                double sx = 0, sy = 0;
                foreach (var p in Points) { sx += p.X; sy += p.Y; }
                int n = Points.Count;
                return new Vector2((float)(sx / n), (float)(sy / n));
            }
            double factor = 1.0 / (6.0 * a);
            return new Vector2((float)(cx * factor), (float)(cy * factor));
        }
    }

    // Result of a DXF import used as input to Voronoi pipeline
    public record DxfImport(Polygon Outer, List<Polygon> Holes, double UnitScale);

    // Parameters bound to the UI — MVP focuses on a subset (seed count, tolerances, rng)
    public record VoronoiParams(
        int SeedCount,
        double PoissonRadius,
        int LloydIterations,
        double ChordTolerance,
        double SimplifyTolerance,
        bool IgnoreHoles,
        int RngSeed);

    // Result after Voronoi + clipping to boundary; cells are polygons
    public record VoronoiResult(Polygon Boundary, List<Polygon> Cells, List<Vector2> Seeds);
}