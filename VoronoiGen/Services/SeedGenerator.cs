using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    // Generates random seeds inside a boundary. MVP implements uniform random + point-in-polygon.
    // Integration: used by VoronoiEngine to compute cells and by the UI to preview seed distribution.
    public static class SeedGenerator
    {
        public static List<SKPoint> GenerateRandom(Polygon boundary, int count, int? rngSeed = null)
        {
            var rand = rngSeed.HasValue ? new Random(rngSeed.Value) : new Random();
            var bounds = boundary.GetBounds();
            var seeds = new List<SKPoint>(count);
            int attempts = 0;
            while (seeds.Count < count && attempts < count * 200)
            {
                attempts++;
                float x = (float)(bounds.Left + rand.NextDouble() * bounds.Width);
                float y = (float)(bounds.Top + rand.NextDouble() * bounds.Height);
                var p = new SKPoint(x, y);
                if (PointInPolygon(p, boundary.Points))
                    seeds.Add(p);
            }
            return seeds;
        }

        // Ray casting point-in-polygon for simple polygons
        public static bool PointInPolygon(SKPoint p, IReadOnlyList<SKPoint> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var pi = poly[i];
                var pj = poly[j];
                bool intersect = ((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                                 (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y + 1e-12) + pi.X);
                if (intersect)
                    inside = !inside;
            }
            return inside;
        }
    }
}
