using SkiaSharp;
using System.Collections.Generic;

namespace VoronoiGen.Models
{
    // Core polygon model used across services and rendering
    public record Polygon(List<SKPoint> Points)
    {
        // Compute axis-aligned bounding box — used for viewBox and random sampling bounds
        public SKRect GetBounds()
        {
            if (Points.Count == 0) return SKRect.Empty;
            float minX = Points[0].X, maxX = Points[0].X, minY = Points[0].Y, maxY = Points[0].Y;
            foreach (var p in Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return new SKRect(minX, minY, maxX, maxY);
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
                var reversed = new List<SKPoint>(Points);
                reversed.Reverse();
                return new Polygon(reversed);
            }
            return this;
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
    public record VoronoiResult(Polygon Boundary, List<Polygon> Cells, List<SKPoint> Seeds);
}
