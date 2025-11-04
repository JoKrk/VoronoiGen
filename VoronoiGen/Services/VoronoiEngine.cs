using SkiaSharp;
using System.Collections.Generic;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    // Computes Voronoi diagram via half-plane clipping against the polygon boundary (Sutherland–Hodgman).
    // Avoids external Voronoi libs for MVP and guarantees cells stay within the boundary.
    public static class VoronoiEngine
    {
        public static VoronoiResult Compute(Polygon boundary, List<SKPoint> seeds)
        {
            var openBoundary = ToOpen(boundary.Points);
            var cells = new List<Polygon>(seeds.Count);

            for (int i = 0; i < seeds.Count; i++)
            {
                var cell = new List<SKPoint>(openBoundary);
                var A = seeds[i];
                for (int j = 0; j < seeds.Count; j++)
                {
                    if (i == j) continue;
                    var B = seeds[j];
                    // Half-plane: points X with |X-A|^2 <= |X-B|^2 -> (B-A)·X <= (|B|^2 - |A|^2)/2
                    var nx = B.X - A.X;
                    var ny = B.Y - A.Y;
                    if (System.Math.Abs(nx) < 1e-9 && System.Math.Abs(ny) < 1e-9)
                        continue;
                    var c = (B.X * B.X + B.Y * B.Y - A.X * A.X - A.Y * A.Y) * 0.5f;
                    cell = ClipWithHalfPlane(cell, nx, ny, c);
                    if (cell.Count < 3) break; // early exit if vanished
                }

                if (cell.Count >= 3)
                {
                    // close polygon
                    if (SKPoint.Distance(cell[0], cell[^1]) > 1e-4) cell.Add(cell[0]);
                    cells.Add(new Polygon(cell));
                }
            }

            return new VoronoiResult(boundary, cells, seeds);
        }

        // Keep points where n·X <= c (side closer to seed A)
        private static List<SKPoint> ClipWithHalfPlane(List<SKPoint> poly, float nx, float ny, float c)
        {
            var res = new List<SKPoint>(poly.Count);
            if (poly.Count == 0) return res;

            // iterate edges (wrap around); input is open ring (no duplicate end)
            for (int i = 0; i < poly.Count; i++)
            {
                var curr = poly[i];
                var next = poly[(i + 1) % poly.Count];
                float fc = nx * curr.X + ny * curr.Y - c;
                float fn = nx * next.X + ny * next.Y - c;
                bool ic = fc <= 1e-6f;
                bool inn = fn <= 1e-6f;

                if (ic && inn)
                {
                    // in -> in : keep next
                    res.Add(next);
                }
                else if (ic && !inn)
                {
                    // in -> out : keep intersection
                    var inter = IntersectAtZero(curr, next, fc, fn);
                    res.Add(inter);
                }
                else if (!ic && inn)
                {
                    // out -> in : add intersection then next
                    var inter = IntersectAtZero(curr, next, fc, fn);
                    res.Add(inter);
                    res.Add(next);
                }
                else
                {
                    // out -> out : keep nothing
                }
            }

            // remove potential duplicate consecutive points
            for (int i = res.Count - 2; i >= 0; i--)
            {
                if (SKPoint.Distance(res[i], res[i + 1]) < 1e-6f)
                    res.RemoveAt(i + 1);
            }
            return res;
        }

        private static SKPoint IntersectAtZero(SKPoint a, SKPoint b, float fa, float fb)
        {
            // Solve fa + t*(fb-fa) = 0 => t = fa/(fa-fb)
            float t = fa / (fa - fb);
            return new SKPoint(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
        }

        private static List<SKPoint> ToOpen(IReadOnlyList<SKPoint> pts)
        {
            if (pts.Count == 0) return new List<SKPoint>();
            bool closed = SKPoint.Distance(pts[0], pts[^1]) < 1e-6f;
            var count = closed ? pts.Count - 1 : pts.Count;
            var list = new List<SKPoint>(count);
            for (int i = 0; i < count; i++) list.Add(pts[i]);
            return list;
        }
    }
}
