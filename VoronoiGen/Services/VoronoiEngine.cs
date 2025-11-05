using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Clipper2Lib;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    /// <summary>
    /// Voronoi generation inside a polygon with optional holes, with support for
    /// Lloyd relaxation, boundary/hole offsets, cell spacing (gap), and smoothing.
    /// </summary>
    public static class VoronoiEngine
    {
        // Public API used by Home.razor — kept compatible, plus optional cancellation.
        public static VoronoiResult Compute(
            Polygon boundary,
            List<Polygon>? holes,
            bool ignoreHoles,
            List<Vector2> seeds,
            double outerOffset,
            double innerOffset,
            double cellGap = 0,
            int smoothIterations = 0,
            double minCellArea = 0,
            double maxAspectRatio = 0,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            var (workOuter, workHoles) = BuildWorkingRegion(boundary, holes, ignoreHoles, outerOffset, innerOffset);

            var filteredSeeds = FilterBoundarySeeds(seeds, workOuter, workHoles, minDistanceFromEdge: cellGap * 0.5);

            var rawCells = ComputeCells(workOuter, workHoles, ignoreHoles, filteredSeeds, token);

            var processed = PostProcessCells(rawCells, workOuter, workHoles, ignoreHoles, cellGap, smoothIterations, minCellArea, maxAspectRatio, token);

            token.ThrowIfCancellationRequested();

            return new VoronoiResult(workOuter, processed, filteredSeeds);
        }

        public static VoronoiResult ComputeWithLloyd(
            Polygon boundary,
            List<Polygon>? holes,
            bool ignoreHoles,
            List<Vector2> seeds,
            int iterations,
            double outerOffset,
            double innerOffset,
            double cellGap = 0,
            int smoothIterations = 0,
            double minCellArea = 0,
            double maxAspectRatio = 0,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            var (workOuter, workHoles) = BuildWorkingRegion(boundary, holes, ignoreHoles, outerOffset, innerOffset);
            var currentSeeds = new List<Vector2>(seeds);

            for (int it = 0; it < iterations; it++)
            {
                token.ThrowIfCancellationRequested();

                var cells = ComputeCells(workOuter, workHoles, ignoreHoles, currentSeeds, token);

                for (int i = 0; i < currentSeeds.Count; i++)
                {
                    token.ThrowIfCancellationRequested();

                    var poly = cells[i];
                    if (poly.Points.Count >= 3)
                    {
                        var c = poly.Centroid();
                        if (SeedGenerator.PointInRegion(c, workOuter, workHoles, ignoreHoles))
                            currentSeeds[i] = c;
                        else
                            currentSeeds[i] = NudgeInside(currentSeeds[i], c, workOuter, workHoles, ignoreHoles);
                    }
                }
            }

            var finalCells = ComputeCells(workOuter, workHoles, ignoreHoles, currentSeeds, token);
            var processed = PostProcessCells(finalCells, workOuter, workHoles, ignoreHoles, cellGap, smoothIterations, minCellArea, maxAspectRatio, token);

            token.ThrowIfCancellationRequested();
            return new VoronoiResult(workOuter, processed, currentSeeds);
        }

        // --- Core Voronoi by half-plane clipping ---

        private static List<Polygon> ComputeCells(
            Polygon workOuter,
            List<Polygon>? workHoles,
            bool ignoreHoles,
            List<Vector2> seeds,
            CancellationToken token)
        {
            var result = new List<Polygon>(seeds.Count);
            var regionPaths = BuildRegionPaths(workOuter, workHoles, ignoreHoles);

            for (int i = 0; i < seeds.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var si = seeds[i];
                var cell = new List<Vector2>(workOuter.Points);

                for (int j = 0; j < seeds.Count; j++)
                {
                    if (j == i) continue;

                    // Lightweight cancellation check every few iterations
                    if ((j & 7) == 0) token.ThrowIfCancellationRequested();

                    var sj = seeds[j];
                    var m = 0.5f * (si + sj);
                    var n = sj - si;
                    cell = ClipWithHalfPlane(cell, m, n);
                    if (cell.Count < 3) break;
                }

                var clipped = ClipPolygonToRegion(cell, regionPaths, si);
                result.Add(clipped);
            }

            return result;
        }

        // Fix for CS1628: copy 'in' params to locals before lambda usage
        private static List<Vector2> ClipWithHalfPlane(IReadOnlyList<Vector2> poly, in Vector2 m, in Vector2 n, float eps = 1e-6f)
        {
            var output = new List<Vector2>(poly.Count);
            if (poly.Count == 0) return output;

            var localM = m;
            var localN = n;

            bool Inside(in Vector2 p)
                => Vector2.Dot(p - localM, localN) <= eps;

            Vector2 Intersection(in Vector2 a, in Vector2 b)
            {
                var ab = b - a;
                float da = Vector2.Dot(a - localM, localN);
                float db = Vector2.Dot(b - localM, localN);
                float denom = da - db;
                float t = Math.Abs(denom) < 1e-12f ? 0f : (da / denom);
                t = Math.Clamp(t, 0f, 1f);
                return a + t * ab;
            }

            var prev = poly[^1];
            bool prevInside = Inside(prev);

            for (int i = 0; i < poly.Count; i++)
            {
                var curr = poly[i];
                bool currInside = Inside(curr);

                if (prevInside && currInside)
                {
                    output.Add(curr);
                }
                else if (prevInside && !currInside)
                {
                    output.Add(Intersection(prev, curr));
                }
                else if (!prevInside && currInside)
                {
                    output.Add(Intersection(prev, curr));
                    output.Add(curr);
                }

                prev = curr;
                prevInside = currInside;
            }

            DedupLinear(output);
            return output;
        }

        // --- Region building & clipping ---

        private static (Polygon workOuter, List<Polygon>? workHoles) BuildWorkingRegion(
            Polygon boundary,
            List<Polygon>? holes,
            bool ignoreHoles,
            double outerOffset,
            double innerOffset)
        {
            // Positive outerOffset means "margin away from boundary" => inset (shrink) the outer polygon
            var workOuter = outerOffset != 0 ? GeometryUtils.OffsetPolygon(boundary, -outerOffset) : boundary;
            List<Polygon>? workHoles = null;

            if (!ignoreHoles && holes is not null && holes.Count > 0)
            {
                // Positive innerOffset grows holes outward, reducing free area — already correct
                workHoles = innerOffset != 0
                    ? holes.Select(h => GeometryUtils.OffsetPolygon(h, innerOffset)).ToList()
                    : new List<Polygon>(holes);
            }

            return (workOuter, workHoles);
        }

        private static Paths64 BuildRegionPaths(Polygon outer, List<Polygon>? holes, bool ignoreHoles)
        {
            var outerPath = new Paths64 { GeometryUtils.ToPath64(outer.Points) };
            if (ignoreHoles || holes is null || holes.Count == 0)
                return outerPath;

            var holePaths = new Paths64();
            foreach (var h in holes)
                holePaths.Add(GeometryUtils.ToPath64(h.Points));

            var c = new Clipper64();
            c.AddSubject(outerPath);
            c.AddClip(holePaths);
            var solution = new Paths64();
            c.Execute(ClipType.Difference, FillRule.NonZero, solution);
            return solution;
        }

        private static Polygon ClipPolygonToRegion(List<Vector2> poly, Paths64 regionPaths, in Vector2 seed)
        {
            if (poly.Count < 3)
                return new Polygon(new List<Vector2>());

            var subject = new Paths64 { GeometryUtils.ToPath64(poly) };
            var c = new Clipper64();
            c.AddSubject(subject);
            c.AddClip(regionPaths);
            var solution = new Paths64();
            c.Execute(ClipType.Intersection, FillRule.NonZero, solution);

            if (solution.Count == 0)
                return new Polygon(new List<Vector2>());

            List<Vector2>? chosen = null;
            double bestArea = double.NegativeInfinity;

            foreach (var path in solution)
            {
                var list = GeometryUtils.ToVector2List(path);
                if (list.Count < 3) continue;

                if (SeedGenerator.PointInPolygon(seed, list))
                {
                    chosen = list;
                    break;
                }

                double area = Math.Abs(Clipper.Area(path));
                if (area > bestArea)
                {
                    bestArea = area;
                    chosen = list;
                }
            }

            return new Polygon(chosen ?? GeometryUtils.ToVector2List(solution[0]));
        }

        // --- Post-processing ---

        private static List<Polygon> PostProcessCells(
            List<Polygon> cells,
            Polygon workOuter,
            List<Polygon>? workHoles,
            bool ignoreHoles,
            double cellGap,
            int smoothIterations,
            double minCellArea,
            double maxAspectRatio,
            CancellationToken token)
        {
            var regionPaths = BuildRegionPaths(workOuter, workHoles, ignoreHoles);
            var processed = new List<Polygon>(cells.Count);

            double effectiveOffset = cellGap > 0 ? -(cellGap * 0.5) : 0;

            foreach (var cell in cells)
            {
                token.ThrowIfCancellationRequested();

                var poly = cell.Points;

                if (effectiveOffset != 0 && poly.Count >= 3)
                {
                    poly = GeometryUtils.OffsetPolygon(new Polygon(poly), effectiveOffset).Points;
                }

                if (smoothIterations > 0 && poly.Count >= 3)
                {
                    poly = ChaikinSmooth(poly, smoothIterations, 0.25f);
                }

                var clipped = ClipPolygonToRegion(poly, regionPaths, seed: cell.Centroid());

                // Filter out cells below minimum area threshold
                if (minCellArea > 0)
                {
                    double area = Math.Abs(clipped.SignedArea());
                    if (area < minCellArea)
                        continue;
                }

                // Filter out cells with aspect ratio exceeding maximum
                if (maxAspectRatio > 0)
                {
                    double aspectRatio = CalculateAspectRatio(clipped);
                    if (aspectRatio > maxAspectRatio)
                        continue;
                }

                processed.Add(clipped);
            }

            return processed;
        }

        private static List<Vector2> ChaikinSmooth(IReadOnlyList<Vector2> pts, int iterations, float weight)
        {
            if (pts.Count < 3 || iterations <= 0) return pts.ToList();

            List<Vector2> work = pts.ToList();

            for (int it = 0; it < iterations; it++)
            {
                var next = new List<Vector2>(work.Count * 2);
                for (int i = 0; i < work.Count; i++)
                {
                    var a = work[i];
                    var b = work[(i + 1) % work.Count];

                    var q = (1 - weight) * a + weight * b;
                    var r = weight * a + (1 - weight) * b;

                    next.Add(q);
                    next.Add(r);
                }
                DedupLinear(next);
                work = next;
            }

            return work;
        }

        private static Vector2 NudgeInside(Vector2 from, Vector2 target, Polygon outer, List<Polygon>? holes, bool ignoreHoles)
        {
            if (SeedGenerator.PointInRegion(from, outer, holes, ignoreHoles))
            {
                Vector2 lo = from, hi = target;
                for (int i = 0; i < 32; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    if (SeedGenerator.PointInRegion(mid, outer, holes, ignoreHoles))
                        lo = mid;
                    else
                        hi = mid;
                }
                return lo;
            }
            return from;
        }

        private static void DedupLinear(List<Vector2> pts, float eps = 1e-5f)
        {
            if (pts.Count < 2) return;
            int w = 1;
            for (int i = 1; i < pts.Count; i++)
            {
                if (Vector2.DistanceSquared(pts[i], pts[w - 1]) > eps * eps)
                {
                    if (w != i) pts[w] = pts[i];
                    w++;
                }
            }
            if (w < pts.Count) pts.RemoveRange(w, pts.Count - w);
        }

        private static List<Vector2> FilterBoundarySeeds(
            List<Vector2> seeds,
            Polygon boundary,
            List<Polygon>? holes,
            double minDistanceFromEdge)
        {
            if (minDistanceFromEdge <= 0) return seeds;

            var filtered = new List<Vector2>(seeds.Count);

            foreach (var seed in seeds)
            {
                bool tooClose = false;

                // Check distance to outer boundary
                if (DistanceToPolygon(seed, boundary.Points) < minDistanceFromEdge)
                    continue;

                // Check distance to holes
                if (holes != null)
                {
                    foreach (var hole in holes)
                    {
                        if (DistanceToPolygon(seed, hole.Points) < minDistanceFromEdge)
                        {
                            tooClose = true;
                            break;
                        }
                    }
                }

                if (!tooClose)
                    filtered.Add(seed);
            }

            return filtered;
        }

        private static double DistanceToPolygon(Vector2 point, IReadOnlyList<Vector2> poly)
        {
            double minDist = double.MaxValue;

            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var dist = DistanceToSegment(point, poly[j], poly[i]);
                minDist = Math.Min(minDist, dist);
            }

            return minDist;
        }

        private static double CalculateAspectRatio(Polygon cell)
        {
            var bounds = cell.GetBounds();
            double width = bounds.Width;
            double height = bounds.Height;

            if (height < 1e-6) return double.MaxValue;
            return Math.Max(width / height, height / width);
        }

        private static double DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var ap = p - a;
            float t = Math.Clamp(Vector2.Dot(ap, ab) / Vector2.Dot(ab, ab), 0f, 1f);
            var closest = a + t * ab;
            return Vector2.Distance(p, closest);
        }
    }
}