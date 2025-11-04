using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    // Computes Voronoi diagram via half-plane clipping against polygon-with-holes and optional offsets.
    // Offsets only affect cell clipping; the returned boundary is always the original.
    public static class VoronoiEngine
    {
        public static VoronoiResult Compute(Polygon boundary, List<Vector2> seeds)
        {
            return Compute(boundary, holes: null, ignoreHoles: true, seeds: seeds, outerOffset: 0, innerOffset: 0);
        }

        public static VoronoiResult ComputeWithLloyd(Polygon boundary, List<Vector2> seeds, int iterations)
        {
            return ComputeWithLloyd(boundary, holes: null, ignoreHoles: true, seeds: seeds, iterations: iterations, outerOffset: 0, innerOffset: 0);
        }

        // Offsets:
        // - outerOffset: inset-only (positive values are clamped to 0 so cells never extend outside the original boundary)
        // - innerOffset: positive grows holes outward (larger forbidden regions)
        public static VoronoiResult Compute(Polygon boundary, List<Polygon>? holes, bool ignoreHoles, List<Vector2> seeds, double outerOffset, double innerOffset)
        {
            // Enforce: never allow cells outside the original boundary.
            // 1) Clamp outer offset to inset-only.
            var effectiveOuterOffset = Math.Min(outerOffset, 0.0);
            var clipOuter = effectiveOuterOffset == 0 ? boundary : GeometryUtils.OffsetPolygon(boundary, effectiveOuterOffset);

            // 2) Holes may grow with innerOffset (positive = grow outward).
            var clipHoles = (!ignoreHoles && holes is { Count: > 0 })
                ? holes.Select(h => innerOffset == 0 ? h : GeometryUtils.OffsetPolygon(h, innerOffset)).ToList()
                : new List<Polygon>();

            var openBoundary = ToOpen(clipOuter.Points)
                .Select(p => (X: (double)p.X, Y: (double)p.Y))
                .ToList();

            var originalOpen = ToOpen(boundary.Points); // used to enforce final inside-clip to original boundary

            var cells = new List<Polygon>(seeds.Count);

            for (int i = 0; i < seeds.Count; i++)
            {
                var cell = new List<(double X, double Y)>(openBoundary);
                var A = seeds[i];

                // Voronoi half-plane clipping
                for (int j = 0; j < seeds.Count; j++)
                {
                    if (i == j) continue;
                    var B = seeds[j];

                    // Half-plane: points X with |X-A|^2 <= |X-B|^2 -> (B-A)·X <= (|B|^2 - |A|^2)/2
                    double nx = (double)B.X - (double)A.X;
                    double ny = (double)B.Y - (double)A.Y;
                    if (Math.Abs(nx) < 1e-12 && Math.Abs(ny) < 1e-12)
                        continue;
                    double c = ((double)B.X * B.X + (double)B.Y * B.Y - (double)A.X * A.X - (double)A.Y * A.Y) * 0.5;

                    cell = ClipWithHalfPlaneD(cell, nx, ny, c);
                    if (cell.Count < 3) break; // early exit if vanished
                }

                if (cell.Count >= 3)
                {
                    // Remove holes by clipping them out (keep outside each hole)
                    if (clipHoles.Count > 0)
                    {
                        foreach (var hole in clipHoles)
                        {
                            var openHole = ToOpen(hole.Points);
                            if (openHole.Count < 3) continue;
                            cell = ClipOutsidePolygon(cell, openHole);
                            if (cell.Count < 3) break;
                        }
                    }

                    // Final safety: force cells to lie inside the ORIGINAL boundary (never outside)
                    if (cell.Count >= 3 && originalOpen.Count >= 3)
                    {
                        cell = ClipInsidePolygon(cell, originalOpen);
                    }

                    if (cell.Count >= 3)
                    {
                        // close polygon if not closed
                        var first = cell[0];
                        var last = cell[^1];
                        if (DistanceD(first, last) > 1e-4)
                            cell.Add(first);

                        cells.Add(new Polygon(cell.Select(p => new Vector2((float)p.X, (float)p.Y)).ToList()));
                    }
                    else
                    {
                        cells.Add(new Polygon(new List<Vector2>()));
                    }
                }
                else
                {
                    cells.Add(new Polygon(new List<Vector2>()));
                }
            }

            // Return ORIGINAL boundary so consumers never see any offset boundaries.
            return new VoronoiResult(boundary, cells, seeds);
        }

        public static VoronoiResult ComputeWithLloyd(Polygon boundary, List<Polygon>? holes, bool ignoreHoles, List<Vector2> seeds, int iterations, double outerOffset, double innerOffset)
        {
            var curSeeds = new List<Vector2>(seeds);
            for (int it = 0; it < iterations; it++)
            {
                var res = Compute(boundary, holes, ignoreHoles, curSeeds, outerOffset, innerOffset);
                // move seeds to centroids; if a cell is empty, keep original
                curSeeds = res.Cells.Select((cell, idx) =>
                {
                    if (cell.Points.Count >= 3)
                    {
                        var c = cell.Centroid();
                        return c;
                    }
                    return curSeeds[idx];
                }).ToList();
            }
            return Compute(boundary, holes, ignoreHoles, curSeeds, outerOffset, innerOffset);
        }

        // Keep points where n·X <= c (side closer to seed A)
        private static List<(double X, double Y)> ClipWithHalfPlaneD(List<(double X, double Y)> poly, double nx, double ny, double c)
        {
            var res = new List<(double X, double Y)>(poly.Count);
            if (poly.Count == 0) return res;

            // iterate edges (wrap around); input is open ring (no duplicate end)
            for (int i = 0; i < poly.Count; i++)
            {
                var curr = poly[i];
                var next = poly[(i + 1) % poly.Count];
                double fc = nx * curr.X + ny * curr.Y - c;
                double fn = nx * next.X + ny * next.Y - c;
                bool ic = fc <= 1e-9;
                bool inn = fn <= 1e-9;

                if (ic && inn)
                {
                    res.Add(next);
                }
                else if (ic && !inn)
                {
                    var inter = IntersectAtZeroD(curr, next, fc, fn);
                    res.Add(inter);
                }
                else if (!ic && inn)
                {
                    var inter = IntersectAtZeroD(curr, next, fc, fn);
                    res.Add(inter);
                    res.Add(next);
                }
            }

            // remove potential duplicate consecutive points
            for (int i = res.Count - 2; i >= 0; i--)
            {
                if (DistanceD(res[i], res[i + 1]) < 1e-9)
                    res.RemoveAt(i + 1);
            }
            return res;
        }

        // Clip polygon against the outside of another polygon (hole): keep points NOT inside the hole.
        private static List<(double X, double Y)> ClipOutsidePolygon(List<(double X, double Y)> poly, IReadOnlyList<Vector2> hole)
        {
            if (poly.Count < 3 || hole.Count < 3) return poly;

            bool holeCcw = SignedArea(hole) > 0;

            var cur = poly;
            for (int i = 0; i < hole.Count; i++)
            {
                var a = hole[i];
                var b = hole[(i + 1) % hole.Count];
                double ex = b.X - a.X;
                double ey = b.Y - a.Y;

                // Use right normal for CCW to keep outside, left normal for CW
                double nx = holeCcw ? ex : -ex;
                double ny = holeCcw ? ey : -ey;
                double onx = ny;      // right normal
                double ony = -nx;

                // keep outside: n·X >= c  => use -n and -c with our <= test
                double c = onx * a.X + ony * a.Y;
                cur = ClipWithHalfPlaneD(cur, -onx, -ony, -c);
                if (cur.Count < 3) break;
            }
            return cur;
        }

        // Clip polygon to the INSIDE of a polygon (Sutherland–Hodgman using cross product sign).
        private static List<(double X, double Y)> ClipInsidePolygon(List<(double X, double Y)> poly, IReadOnlyList<Vector2> clip)
        {
            if (poly.Count < 3 || clip.Count < 3) return poly;

            bool ccw = SignedArea(clip) > 0;
            var cur = poly;

            for (int i = 0; i < clip.Count; i++)
            {
                var a = clip[i];
                var b = clip[(i + 1) % clip.Count];

                double ex = b.X - a.X;
                double ey = b.Y - a.Y;

                // signed side function using cross(e, p-a)
                double Side((double X, double Y) p) => ex * (p.Y - a.Y) - ey * (p.X - a.X);

                var nextPoly = new List<(double X, double Y)>(cur.Count);
                for (int k = 0; k < cur.Count; k++)
                {
                    var p = cur[k];
                    var q = cur[(k + 1) % cur.Count];

                    double fp = Side(p);
                    double fq = Side(q);

                    bool inP = ccw ? fp >= -1e-9 : fp <= 1e-9; // inside is left-of-edge for CCW
                    bool inQ = ccw ? fq >= -1e-9 : fq <= 1e-9;

                    if (inP && inQ)
                    {
                        nextPoly.Add(q);
                    }
                    else if (inP && !inQ)
                    {
                        var inter = IntersectAtZeroD(p, q, fp, fq);
                        nextPoly.Add(inter);
                    }
                    else if (!inP && inQ)
                    {
                        var inter = IntersectAtZeroD(p, q, fp, fq);
                        nextPoly.Add(inter);
                        nextPoly.Add(q);
                    }
                }

                cur = nextPoly;
                if (cur.Count < 3) break;
            }

            // remove potential duplicate consecutive points
            for (int i = cur.Count - 2; i >= 0; i--)
            {
                if (DistanceD(cur[i], cur[i + 1]) < 1e-9)
                    cur.RemoveAt(i + 1);
            }

            return cur;
        }

        private static double SignedArea(IReadOnlyList<Vector2> pts)
        {
            double a = 0;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                a += (double)(pts[j].X * pts[i].Y - pts[i].X * pts[j].Y);
            }
            return 0.5 * a;
        }

        private static (double X, double Y) IntersectAtZeroD((double X, double Y) a, (double X, double Y) b, double fa, double fb)
        {
            // Solve fa + t*(fb-fa) = 0 => t = fa/(fa-fb)
            double t = fa / (fa - fb);
            return (a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
        }

        private static double DistanceD((double X, double Y) a, (double X, double Y) b)
        {
            var dx = a.X - b.X; var dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy);
        }

        private static List<Vector2> ToOpen(IReadOnlyList<Vector2> pts)
        {
            if (pts.Count == 0) return new List<Vector2>();
            bool closed = Vector2.Distance(pts[0], pts[^1]) < 1e-6f;
            var count = closed ? pts.Count - 1 : pts.Count;
            var list = new List<Vector2>(count);
            for (int i = 0; i < count; i++) list.Add(pts[i]);
            return list;
        }
    }
}