using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Clipper2Lib;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    public readonly record struct GenerationProgress(double Percent, string Stage, string Detail);

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
            double roundRadius = 0,           // new: absolute fillet radius (0 = auto)
            float chaikinWeight = 0.25f,      // new: Chaikin split weight (0..0.5)
            double roundArcTolerance = 0,     // new: arc tessellation tolerance (0 = auto)
            CancellationToken token = default,
            IProgress<GenerationProgress>? progress = null)
        {
            token.ThrowIfCancellationRequested();
            ReportProgress(progress, 0.10, "Preparing geometry", "Applying boundary and hole offsets");

            var (workOuter, workHoles) = BuildWorkingRegion(boundary, holes, ignoreHoles, outerOffset, innerOffset);

            ReportProgress(progress, 0.14, "Filtering seeds", $"Checking {seeds.Count:N0} seed points");
            var filteredSeeds = FilterBoundarySeeds(seeds, workOuter, workHoles, minDistanceFromEdge: cellGap * 0.5);

            var rawCells = ComputeCells(workOuter, workHoles, ignoreHoles, filteredSeeds, token, progress, 0.18, 0.62, "Computing Voronoi cells");

            var processed = PostProcessCells(
                rawCells, workOuter, workHoles, ignoreHoles,
                cellGap, smoothIterations, minCellArea, maxAspectRatio,
                roundRadius, chaikinWeight, roundArcTolerance,
                token, progress, 0.80, 0.18);

            token.ThrowIfCancellationRequested();
            ReportProgress(progress, 1.0, "Complete", $"Generated {processed.Count:N0} cells");

            return new VoronoiResult(boundary, workOuter, processed, filteredSeeds);
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
            double roundRadius = 0,           // new
            float chaikinWeight = 0.25f,      // new
            double roundArcTolerance = 0,     // new
            CancellationToken token = default,
            IProgress<GenerationProgress>? progress = null)
        {
            token.ThrowIfCancellationRequested();
            ReportProgress(progress, 0.10, "Preparing geometry", "Applying boundary and hole offsets");

            var (workOuter, workHoles) = BuildWorkingRegion(boundary, holes, ignoreHoles, outerOffset, innerOffset);
            var currentSeeds = new List<Vector2>(seeds);
            var lloydWeight = iterations > 0 ? 0.58 / iterations : 0;

            for (int it = 0; it < iterations; it++)
            {
                token.ThrowIfCancellationRequested();

                var iterationBase = 0.16 + (it * lloydWeight);
                var cells = ComputeCells(
                    workOuter, workHoles, ignoreHoles, currentSeeds, token, progress,
                    iterationBase, lloydWeight * 0.82, $"Lloyd relaxation {it + 1}/{iterations}");

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

                    if ((i & 31) == 0)
                    {
                        var movePercent = iterationBase + (lloydWeight * 0.82) + (lloydWeight * 0.18 * ((i + 1) / (double)Math.Max(1, currentSeeds.Count)));
                        ReportProgress(progress, movePercent, $"Lloyd relaxation {it + 1}/{iterations}", $"Moving seed {i + 1:N0} of {currentSeeds.Count:N0}");
                    }
                }
            }

            var finalCells = ComputeCells(workOuter, workHoles, ignoreHoles, currentSeeds, token, progress, 0.74, 0.16, "Computing final cells");
            var processed = PostProcessCells(
                finalCells, workOuter, workHoles, ignoreHoles,
                cellGap, smoothIterations, minCellArea, maxAspectRatio,
                roundRadius, chaikinWeight, roundArcTolerance,
                token, progress, 0.90, 0.08);

            token.ThrowIfCancellationRequested();
            ReportProgress(progress, 1.0, "Complete", $"Generated {processed.Count:N0} cells");
            return new VoronoiResult(boundary, workOuter, processed, currentSeeds);
        }

        public static async Task<VoronoiResult> ComputeAsync(
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
            double roundRadius = 0,
            float chaikinWeight = 0.25f,
            double roundArcTolerance = 0,
            CancellationToken token = default,
            IProgress<GenerationProgress>? progress = null)
        {
            token.ThrowIfCancellationRequested();
            ReportProgress(progress, 0.10, "Preparing geometry", "Applying boundary and hole offsets");
            await YieldToUi(token);

            var (workOuter, workHoles) = BuildWorkingRegion(boundary, holes, ignoreHoles, outerOffset, innerOffset);

            ReportProgress(progress, 0.14, "Filtering seeds", $"Checking {seeds.Count:N0} seed points");
            await YieldToUi(token);
            var filteredSeeds = FilterBoundarySeeds(seeds, workOuter, workHoles, minDistanceFromEdge: cellGap * 0.5);

            var rawCells = await ComputeCellsAsync(workOuter, workHoles, ignoreHoles, filteredSeeds, token, progress, 0.18, 0.62, "Computing Voronoi cells");

            var processed = await PostProcessCellsAsync(
                rawCells, workOuter, workHoles, ignoreHoles,
                cellGap, smoothIterations, minCellArea, maxAspectRatio,
                roundRadius, chaikinWeight, roundArcTolerance,
                token, progress, 0.80, 0.18);

            token.ThrowIfCancellationRequested();
            ReportProgress(progress, 1.0, "Complete", $"Generated {processed.Count:N0} cells");
            await YieldToUi(token);

            return new VoronoiResult(boundary, workOuter, processed, filteredSeeds);
        }

        public static async Task<VoronoiResult> ComputeWithLloydAsync(
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
            double roundRadius = 0,
            float chaikinWeight = 0.25f,
            double roundArcTolerance = 0,
            CancellationToken token = default,
            IProgress<GenerationProgress>? progress = null)
        {
            token.ThrowIfCancellationRequested();
            ReportProgress(progress, 0.10, "Preparing geometry", "Applying boundary and hole offsets");
            await YieldToUi(token);

            var (workOuter, workHoles) = BuildWorkingRegion(boundary, holes, ignoreHoles, outerOffset, innerOffset);
            var currentSeeds = new List<Vector2>(seeds);
            var lloydWeight = iterations > 0 ? 0.58 / iterations : 0;

            for (int it = 0; it < iterations; it++)
            {
                token.ThrowIfCancellationRequested();

                var iterationBase = 0.16 + (it * lloydWeight);
                var cells = await ComputeCellsAsync(
                    workOuter, workHoles, ignoreHoles, currentSeeds, token, progress,
                    iterationBase, lloydWeight * 0.82, $"Lloyd relaxation {it + 1}/{iterations}");

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

                    if ((i & 31) == 0)
                    {
                        var movePercent = iterationBase + (lloydWeight * 0.82) + (lloydWeight * 0.18 * ((i + 1) / (double)Math.Max(1, currentSeeds.Count)));
                        ReportProgress(progress, movePercent, $"Lloyd relaxation {it + 1}/{iterations}", $"Moving seed {i + 1:N0} of {currentSeeds.Count:N0}");
                        await YieldToUi(token);
                    }
                }
            }

            var finalCells = await ComputeCellsAsync(workOuter, workHoles, ignoreHoles, currentSeeds, token, progress, 0.74, 0.16, "Computing final cells");
            var processed = await PostProcessCellsAsync(
                finalCells, workOuter, workHoles, ignoreHoles,
                cellGap, smoothIterations, minCellArea, maxAspectRatio,
                roundRadius, chaikinWeight, roundArcTolerance,
                token, progress, 0.90, 0.08);

            token.ThrowIfCancellationRequested();
            ReportProgress(progress, 1.0, "Complete", $"Generated {processed.Count:N0} cells");
            await YieldToUi(token);
            return new VoronoiResult(boundary, workOuter, processed, currentSeeds);
        }

        // --- Core Voronoi by half-plane clipping ---

        private static List<Polygon> ComputeCells(
            Polygon workOuter,
            List<Polygon>? workHoles,
            bool ignoreHoles,
            List<Vector2> seeds,
            CancellationToken token,
            IProgress<GenerationProgress>? progress = null,
            double basePercent = 0,
            double weight = 1,
            string stage = "Computing Voronoi cells")
        {
            var result = new List<Polygon>(seeds.Count);
            var regionPaths = BuildRegionPaths(workOuter, workHoles, ignoreHoles);
            var reportEvery = Math.Max(1, seeds.Count / 100);

            for (int i = 0; i < seeds.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                if (i == 0 || (i % reportEvery) == 0)
                {
                    ReportProgress(progress, basePercent + (weight * (i / (double)Math.Max(1, seeds.Count))), stage, $"Cell {i + 1:N0} of {seeds.Count:N0}");
                }

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

            ReportProgress(progress, basePercent + weight, stage, $"Computed {seeds.Count:N0} cells");
            return result;
        }

        private static async Task<List<Polygon>> ComputeCellsAsync(
            Polygon workOuter,
            List<Polygon>? workHoles,
            bool ignoreHoles,
            List<Vector2> seeds,
            CancellationToken token,
            IProgress<GenerationProgress>? progress = null,
            double basePercent = 0,
            double weight = 1,
            string stage = "Computing Voronoi cells")
        {
            var result = new List<Polygon>(seeds.Count);
            var regionPaths = BuildRegionPaths(workOuter, workHoles, ignoreHoles);
            var reportEvery = Math.Max(1, seeds.Count / 100);

            for (int i = 0; i < seeds.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                if (i == 0 || (i % reportEvery) == 0)
                {
                    ReportProgress(progress, basePercent + (weight * (i / (double)Math.Max(1, seeds.Count))), stage, $"Cell {i + 1:N0} of {seeds.Count:N0}");
                    await YieldToUi(token);
                }

                var si = seeds[i];
                var cell = new List<Vector2>(workOuter.Points);

                for (int j = 0; j < seeds.Count; j++)
                {
                    if (j == i) continue;

                    if ((j & 7) == 0) token.ThrowIfCancellationRequested();
                    if ((j & 127) == 0)
                    {
                        ReportProgress(
                            progress,
                            basePercent + (weight * ((i + (j / (double)Math.Max(1, seeds.Count))) / Math.Max(1, seeds.Count))),
                            stage,
                            $"Cell {i + 1:N0} of {seeds.Count:N0}");
                        await YieldToUi(token);
                    }

                    var sj = seeds[j];
                    var m = 0.5f * (si + sj);
                    var n = sj - si;
                    cell = ClipWithHalfPlane(cell, m, n);
                    if (cell.Count < 3) break;
                }

                var clipped = ClipPolygonToRegion(cell, regionPaths, si);
                result.Add(clipped);
            }

            ReportProgress(progress, basePercent + weight, stage, $"Computed {seeds.Count:N0} cells");
            await YieldToUi(token);
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
            double roundRadius,
            float chaikinWeight,
            double roundArcTolerance,
            CancellationToken token,
            IProgress<GenerationProgress>? progress = null,
            double basePercent = 0,
            double weight = 1)
        {
            var regionPaths = BuildRegionPaths(workOuter, workHoles, ignoreHoles);
            var processed = new List<Polygon>(cells.Count);

            double effectiveOffset = cellGap > 0 ? -(cellGap * 0.5) : 0;
            var reportEvery = Math.Max(1, cells.Count / 100);

            for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
            {
                token.ThrowIfCancellationRequested();

                if (cellIndex == 0 || (cellIndex % reportEvery) == 0)
                {
                    ReportProgress(progress, basePercent + (weight * (cellIndex / (double)Math.Max(1, cells.Count))), "Processing cells", $"Cell {cellIndex + 1:N0} of {cells.Count:N0}");
                }

                var cell = cells[cellIndex];
                var poly = cell.Points;

                // 1) Apply gap as an inset; we’ll use round-corner smoothing afterward.
                if (effectiveOffset != 0 && poly.Count >= 3)
                {
                    poly = GeometryUtils.OffsetPolygon(new Polygon(poly), effectiveOffset).Points;
                }

                // 2) Corner-rounding pass with constant-radius opening (erode then dilate).
                //    This removes sharp corners while largely preserving bulk geometry.
                bool wantRound = (smoothIterations > 0) || (roundRadius > 0);
                if (wantRound && poly.Count >= 3)
                {
                    // Estimate a conservative radius:
                    // - If explicit radius provided, use it.
                    // - Else if there’s a gap, tie base radius to that.
                    // - Otherwise, derive from local average edge length.
                    double avgLen = AverageEdgeLength(poly);

                    double baseR = (cellGap > 0)
                        ? Math.Abs(effectiveOffset)
                        : Math.Max(1e-3, 0.15 * avgLen);

                    double r = (roundRadius > 0)
                        ? roundRadius
                        : Math.Clamp(smoothIterations * baseR, 0, 0.45 * avgLen);

                    if (r > 1e-5)
                    {
                        double arcTol = (roundArcTolerance > 0)
                            ? roundArcTolerance
                            : Math.Max(0.25, r / 6.0);

                        poly = RoundCornersOpening(poly, r, arcTol);

                        // Optional light Chaikin pass to even arc tessellation.
                        poly = ChaikinSmooth(poly, 1, Math.Clamp(chaikinWeight, 0.01f, 0.49f));
                    }
                    else
                    {
                        // Fallback to Chaikin only (very small radius).
                        poly = ChaikinSmooth(poly, Math.Max(0, smoothIterations), Math.Clamp(chaikinWeight, 0.01f, 0.49f));
                    }
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

            ReportProgress(progress, basePercent + weight, "Processing cells", $"Kept {processed.Count:N0} cells");
            return processed;
        }

        private static async Task<List<Polygon>> PostProcessCellsAsync(
            List<Polygon> cells,
            Polygon workOuter,
            List<Polygon>? workHoles,
            bool ignoreHoles,
            double cellGap,
            int smoothIterations,
            double minCellArea,
            double maxAspectRatio,
            double roundRadius,
            float chaikinWeight,
            double roundArcTolerance,
            CancellationToken token,
            IProgress<GenerationProgress>? progress = null,
            double basePercent = 0,
            double weight = 1)
        {
            var regionPaths = BuildRegionPaths(workOuter, workHoles, ignoreHoles);
            var processed = new List<Polygon>(cells.Count);

            double effectiveOffset = cellGap > 0 ? -(cellGap * 0.5) : 0;
            var reportEvery = Math.Max(1, cells.Count / 100);

            for (int cellIndex = 0; cellIndex < cells.Count; cellIndex++)
            {
                token.ThrowIfCancellationRequested();

                if (cellIndex == 0 || (cellIndex % reportEvery) == 0)
                {
                    ReportProgress(progress, basePercent + (weight * (cellIndex / (double)Math.Max(1, cells.Count))), "Processing cells", $"Cell {cellIndex + 1:N0} of {cells.Count:N0}");
                    await YieldToUi(token);
                }

                var cell = cells[cellIndex];
                var poly = cell.Points;

                if (effectiveOffset != 0 && poly.Count >= 3)
                {
                    poly = GeometryUtils.OffsetPolygon(new Polygon(poly), effectiveOffset).Points;
                }

                bool wantRound = (smoothIterations > 0) || (roundRadius > 0);
                if (wantRound && poly.Count >= 3)
                {
                    double avgLen = AverageEdgeLength(poly);

                    double baseR = (cellGap > 0)
                        ? Math.Abs(effectiveOffset)
                        : Math.Max(1e-3, 0.15 * avgLen);

                    double r = (roundRadius > 0)
                        ? roundRadius
                        : Math.Clamp(smoothIterations * baseR, 0, 0.45 * avgLen);

                    if (r > 1e-5)
                    {
                        double arcTol = (roundArcTolerance > 0)
                            ? roundArcTolerance
                            : Math.Max(0.25, r / 6.0);

                        poly = RoundCornersOpening(poly, r, arcTol);
                        poly = ChaikinSmooth(poly, 1, Math.Clamp(chaikinWeight, 0.01f, 0.49f));
                    }
                    else
                    {
                        poly = ChaikinSmooth(poly, Math.Max(0, smoothIterations), Math.Clamp(chaikinWeight, 0.01f, 0.49f));
                    }
                }

                var clipped = ClipPolygonToRegion(poly, regionPaths, seed: cell.Centroid());
                if ((cellIndex & 3) == 0)
                {
                    await YieldToUi(token);
                }

                if (minCellArea > 0)
                {
                    double area = Math.Abs(clipped.SignedArea());
                    if (area < minCellArea)
                        continue;
                }

                if (maxAspectRatio > 0)
                {
                    double aspectRatio = CalculateAspectRatio(clipped);
                    if (aspectRatio > maxAspectRatio)
                        continue;
                }

                processed.Add(clipped);
            }

            ReportProgress(progress, basePercent + weight, "Processing cells", $"Kept {processed.Count:N0} cells");
            await YieldToUi(token);
            return processed;
        }

        private static void ReportProgress(IProgress<GenerationProgress>? progress, double percent, string stage, string detail)
        {
            progress?.Report(new GenerationProgress(Math.Clamp(percent, 0, 1), stage, detail));
        }

        private static async Task YieldToUi(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(1, token);
            token.ThrowIfCancellationRequested();
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
            var pts = cell.Points;
            if (pts is null || pts.Count == 0) return double.MaxValue;

            // Fallbacks for tiny/degenerate cells
            if (pts.Count < 3)
            {
                var b = cell.GetBounds();
                double w = b.Width, h = b.Height;
                if (w < 1e-9 || h < 1e-9) return double.MaxValue;
                return (w >= h) ? (w / h) : (h / w);
            }

            // PCA on the vertex set to estimate elongation
            double mx = 0, my = 0;
            int n = pts.Count;
            for (int i = 0; i < n; i++) { mx += pts[i].X; my += pts[i].Y; }
            mx /= n; my /= n;

            double sxx = 0, syy = 0, sxy = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = pts[i].X - mx;
                double dy = pts[i].Y - my;
                sxx += dx * dx;
                syy += dy * dy;
                sxy += dx * dy;
            }
            sxx /= n; syy /= n; sxy /= n;

            double tr = sxx + syy;
            double det = sxx * syy - sxy * sxy;
            double disc = Math.Max(0.0, tr * tr - 4.0 * det);
            double root = Math.Sqrt(disc);

            double lMax = 0.5 * (tr + root);
            double lMin = 0.5 * (tr - root);

            if (lMin <= 1e-12) return double.MaxValue; // essentially a line
            double ratio = Math.Sqrt(lMax / lMin);
            return ratio;
        }


        private static double DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var ap = p - a;
            float t = Math.Clamp(Vector2.Dot(ap, ab) / Vector2.Dot(ab, ab), 0f, 1f);
            var closest = a + t * ab;
            return Vector2.Distance(p, closest);
        }

        // --- New helpers: radius-based rounding ---

        private static List<Vector2> RoundCornersOpening(IReadOnlyList<Vector2> pts, double radius, double arcTolerance)
        {
            if (pts.Count < 3 || radius <= 0) return pts.ToList();

            const double SCALE = 1e6;

            var path = new Path64(pts.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                path.Add(new Point64(
                    (long)Math.Round(p.X * SCALE),
                    (long)Math.Round(p.Y * SCALE)
                ));
            }

            var co = new ClipperOffset
            {
                ArcTolerance = Math.Max(1.0, arcTolerance * SCALE),
                MiterLimit = 2.0
            };

            // Erode by r
            var eroded = new Paths64();
            co.AddPath(path, JoinType.Round, EndType.Polygon);
            co.Execute(-radius * SCALE, eroded); // swapped order

            if (eroded.Count == 0) return pts.ToList();

            // Dilate by r
            co.Clear();
            co.AddPaths(eroded, JoinType.Round, EndType.Polygon);
            var opened = new Paths64();
            co.Execute(radius * SCALE, opened); // swapped order

            if (opened.Count == 0) return pts.ToList();

            Path64 best = opened[0];
            double bestArea = Math.Abs(Clipper.Area(best));
            for (int i = 1; i < opened.Count; i++)
            {
                double a = Math.Abs(Clipper.Area(opened[i]));
                if (a > bestArea) { best = opened[i]; bestArea = a; }
            }

            var result = new List<Vector2>(best.Count);
            foreach (var q in best)
                result.Add(new Vector2((float)(q.X / SCALE), (float)(q.Y / SCALE)));

            DedupLinear(result);
            return result;
        }

        private static double AverageEdgeLength(IReadOnlyList<Vector2> poly)
        {
            if (poly.Count < 2) return 0.0;
            double sum = 0.0;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                sum += Vector2.Distance(poly[j], poly[i]);
            return sum / poly.Count;
        }
    }
}
