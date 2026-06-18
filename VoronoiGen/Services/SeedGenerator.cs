using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    // Generates seeds inside a boundary or polygon-with-holes. Adds Poisson-disk sampling for more uniform spacing.
    // Integration: used by Home (Generate) and by VoronoiEngine during Lloyd relaxation.
    public static class SeedGenerator
    {
        public static List<Vector2> GenerateRandom(Polygon boundary, int count, int? rngSeed = null)
        {
            return GenerateRandom(boundary, holes: null, ignoreHoles: true, count: count, rngSeed: rngSeed);
        }

        public static List<Vector2> GenerateRandom(Polygon boundary, List<Polygon>? holes, bool ignoreHoles, int count, int? rngSeed = null, CancellationToken token = default, IProgress<GenerationProgress>? progress = null)
        {
            var rand = rngSeed.HasValue ? new Random(rngSeed.Value) : new Random();
            var bounds = boundary.GetBounds();
            var seeds = new List<Vector2>(count);
            int attempts = 0;
            int maxAttempts = count * 500;

            while (seeds.Count < count && attempts < maxAttempts)
            {
                if ((attempts & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    ReportProgress(progress, seeds.Count / (double)Math.Max(1, count), "Generating fallback seeds", $"{seeds.Count:N0} of {count:N0} seeds");
                }

                attempts++;
                float x = (float)(bounds.Left + rand.NextDouble() * bounds.Width);
                float y = (float)(bounds.Top + rand.NextDouble() * bounds.Height);
                var p = new Vector2(x, y);
                if (InBounds(p, bounds) && PointInRegion(p, boundary, holes, ignoreHoles))
                    seeds.Add(p);
            }

            ReportProgress(progress, 1.0, "Generating fallback seeds", $"{seeds.Count:N0} seeds generated");
            return seeds;
        }

        // Bridson Poisson-disk sampling (2D) adapted to arbitrary polygon via rejection in boundary or boundary-with-holes.
        // radius: minimum distance between points; k: attempts per active point.
        public static List<Vector2> GeneratePoisson(Polygon boundary, float radius, int? rngSeed = null, int k = 30)
        {
            return GeneratePoisson(boundary, holes: null, ignoreHoles: true, radius: radius, rngSeed: rngSeed, k: k);
        }

        public static List<Vector2> GeneratePoisson(Polygon boundary, List<Polygon>? holes, bool ignoreHoles, float radius, int? rngSeed = null, int k = 30, int? maxSamples = null, CancellationToken token = default, IProgress<GenerationProgress>? progress = null)
        {
            var rand = rngSeed.HasValue ? new Random(rngSeed.Value) : new Random();
            var bounds = boundary.GetBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return new List<Vector2>();

            double cellSize = radius / Math.Sqrt(2.0);
            int gridW = Math.Max(1, (int)Math.Ceiling(bounds.Width / cellSize));
            int gridH = Math.Max(1, (int)Math.Ceiling(bounds.Height / cellSize));
            var grid = new int[gridW, gridH];
            for (int i = 0; i < gridW; i++) for (int j = 0; j < gridH; j++) grid[i, j] = -1;

            var samples = new List<Vector2>();
            var active = new List<int>();

            // Helper: grid index
            int IdxX(double x) => (int)Math.Floor((x - bounds.Left) / cellSize);
            int IdxY(double y) => (int)Math.Floor((y - bounds.Top) / cellSize);

            // place initial point inside polygon
            for (int t = 0; t < 20000 && samples.Count == 0; t++)
            {
                if ((t & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    ReportProgress(progress, 0.02, "Generating Poisson seeds", "Finding an initial seed");
                }

                float x = (float)(bounds.Left + rand.NextDouble() * bounds.Width);
                float y = (float)(bounds.Top + rand.NextDouble() * bounds.Height);
                var p = new Vector2(x, y);
                if (InBounds(p, bounds) && PointInRegion(p, boundary, holes, ignoreHoles))
                {
                    samples.Add(p);
                    active.Add(0);
                    int gx = Math.Clamp(IdxX(x), 0, gridW - 1);
                    int gy = Math.Clamp(IdxY(y), 0, gridH - 1);
                    grid[gx, gy] = 0;
                }
            }
            if (samples.Count == 0) return samples;

            var steps = 0;
            while (active.Count > 0 && (!maxSamples.HasValue || samples.Count < maxSamples.Value))
            {
                steps++;
                if ((steps & 63) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    ReportProgress(progress, maxSamples.HasValue ? samples.Count / (double)Math.Max(1, maxSamples.Value) : null, "Generating Poisson seeds", $"{samples.Count:N0} seeds accepted");
                }

                int ai = active[rand.Next(active.Count)];
                var basePt = samples[ai];
                bool found = false;

                for (int i = 0; i < k; i++)
                {
                    // random point in annulus [r, 2r)
                    double ang = rand.NextDouble() * Math.PI * 2.0;
                    double rad = radius * (1.0 + rand.NextDouble());
                    float nx = basePt.X + (float)(rad * Math.Cos(ang));
                    float ny = basePt.Y + (float)(rad * Math.Sin(ang));
                    var cand = new Vector2(nx, ny);

                    if (!InBounds(cand, bounds) || !PointInRegion(cand, boundary, holes, ignoreHoles)) continue;

                    int gx = IdxX(nx); int gy = IdxY(ny);
                    if (gx < 0 || gy < 0 || gx >= gridW || gy >= gridH) continue;

                    bool ok = true;
                    // check neighboring cells within 2
                    for (int ix = Math.Max(0, gx - 2); ix <= Math.Min(gridW - 1, gx + 2); ix++)
                    {
                        for (int iy = Math.Max(0, gy - 2); iy <= Math.Min(gridH - 1, gy + 2); iy++)
                        {
                            int si = grid[ix, iy];
                            if (si == -1) continue;
                            if (Vector2.Distance(samples[si], cand) < radius)
                            { ok = false; break; }
                        }
                        if (!ok) break;
                    }
                    if (!ok) continue;

                    samples.Add(cand);
                    active.Add(samples.Count - 1);
                    grid[gx, gy] = samples.Count - 1;
                    found = true;
                    break;
                }

                if (!found)
                {
                    // retire
                    int idx = active.IndexOf(ai);
                    if (idx >= 0) active.RemoveAt(idx);
                }
            }

            ReportProgress(progress, 1.0, "Generating Poisson seeds", $"{samples.Count:N0} seeds generated");
            return samples;
        }

        public static async Task<List<Vector2>> GenerateRandomAsync(Polygon boundary, List<Polygon>? holes, bool ignoreHoles, int count, int? rngSeed = null, CancellationToken token = default, IProgress<GenerationProgress>? progress = null)
        {
            var rand = rngSeed.HasValue ? new Random(rngSeed.Value) : new Random();
            var bounds = boundary.GetBounds();
            var seeds = new List<Vector2>(count);
            int attempts = 0;
            int maxAttempts = count * 500;

            while (seeds.Count < count && attempts < maxAttempts)
            {
                if ((attempts & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    ReportProgress(progress, seeds.Count / (double)Math.Max(1, count), "Generating fallback seeds", $"{seeds.Count:N0} of {count:N0} seeds");
                    await Task.Delay(1, token);
                }

                attempts++;
                float x = (float)(bounds.Left + rand.NextDouble() * bounds.Width);
                float y = (float)(bounds.Top + rand.NextDouble() * bounds.Height);
                var p = new Vector2(x, y);
                if (InBounds(p, bounds) && PointInRegion(p, boundary, holes, ignoreHoles))
                    seeds.Add(p);
            }

            ReportProgress(progress, 1.0, "Generating fallback seeds", $"{seeds.Count:N0} seeds generated");
            return seeds;
        }

        public static async Task<List<Vector2>> GeneratePoissonAsync(Polygon boundary, List<Polygon>? holes, bool ignoreHoles, float radius, int? rngSeed = null, int k = 30, int? maxSamples = null, CancellationToken token = default, IProgress<GenerationProgress>? progress = null)
        {
            var rand = rngSeed.HasValue ? new Random(rngSeed.Value) : new Random();
            var bounds = boundary.GetBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return new List<Vector2>();

            double cellSize = radius / Math.Sqrt(2.0);
            int gridW = Math.Max(1, (int)Math.Ceiling(bounds.Width / cellSize));
            int gridH = Math.Max(1, (int)Math.Ceiling(bounds.Height / cellSize));
            var grid = new int[gridW, gridH];
            for (int i = 0; i < gridW; i++) for (int j = 0; j < gridH; j++) grid[i, j] = -1;

            var samples = new List<Vector2>();
            var active = new List<int>();

            int IdxX(double x) => (int)Math.Floor((x - bounds.Left) / cellSize);
            int IdxY(double y) => (int)Math.Floor((y - bounds.Top) / cellSize);

            for (int t = 0; t < 20000 && samples.Count == 0; t++)
            {
                if ((t & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    ReportProgress(progress, 0.02, "Generating Poisson seeds", "Finding an initial seed");
                    await Task.Delay(1, token);
                }

                float x = (float)(bounds.Left + rand.NextDouble() * bounds.Width);
                float y = (float)(bounds.Top + rand.NextDouble() * bounds.Height);
                var p = new Vector2(x, y);
                if (InBounds(p, bounds) && PointInRegion(p, boundary, holes, ignoreHoles))
                {
                    samples.Add(p);
                    active.Add(0);
                    int gx = Math.Clamp(IdxX(x), 0, gridW - 1);
                    int gy = Math.Clamp(IdxY(y), 0, gridH - 1);
                    grid[gx, gy] = 0;
                }
            }
            if (samples.Count == 0) return samples;

            var steps = 0;
            while (active.Count > 0 && (!maxSamples.HasValue || samples.Count < maxSamples.Value))
            {
                steps++;
                if ((steps & 63) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    ReportProgress(progress, maxSamples.HasValue ? samples.Count / (double)Math.Max(1, maxSamples.Value) : 0.5, "Generating Poisson seeds", $"{samples.Count:N0} seeds accepted");
                    await Task.Delay(1, token);
                }

                int ai = active[rand.Next(active.Count)];
                var basePt = samples[ai];
                bool found = false;

                for (int i = 0; i < k; i++)
                {
                    double ang = rand.NextDouble() * Math.PI * 2.0;
                    double rad = radius * (1.0 + rand.NextDouble());
                    float nx = basePt.X + (float)(rad * Math.Cos(ang));
                    float ny = basePt.Y + (float)(rad * Math.Sin(ang));
                    var cand = new Vector2(nx, ny);

                    if (!InBounds(cand, bounds) || !PointInRegion(cand, boundary, holes, ignoreHoles)) continue;

                    int gx = IdxX(nx); int gy = IdxY(ny);
                    if (gx < 0 || gy < 0 || gx >= gridW || gy >= gridH) continue;

                    bool ok = true;
                    for (int ix = Math.Max(0, gx - 2); ix <= Math.Min(gridW - 1, gx + 2); ix++)
                    {
                        for (int iy = Math.Max(0, gy - 2); iy <= Math.Min(gridH - 1, gy + 2); iy++)
                        {
                            int si = grid[ix, iy];
                            if (si == -1) continue;
                            if (Vector2.Distance(samples[si], cand) < radius)
                            { ok = false; break; }
                        }
                        if (!ok) break;
                    }
                    if (!ok) continue;

                    samples.Add(cand);
                    active.Add(samples.Count - 1);
                    grid[gx, gy] = samples.Count - 1;
                    found = true;
                    break;
                }

                if (!found)
                {
                    int idx = active.IndexOf(ai);
                    if (idx >= 0) active.RemoveAt(idx);
                }
            }

            ReportProgress(progress, 1.0, "Generating Poisson seeds", $"{samples.Count:N0} seeds generated");
            return samples;
        }

        private static void ReportProgress(IProgress<GenerationProgress>? progress, double? percent, string stage, string detail)
        {
            progress?.Report(new GenerationProgress(percent ?? 0, stage, detail));
        }

        public static bool PointInRegion(in Vector2 p, Polygon outer, List<Polygon>? holes, bool ignoreHoles)
        {
            if (!PointInPolygon(p, outer.Points)) return false;
            if (ignoreHoles || holes is null || holes.Count == 0) return true;
            foreach (var h in holes)
            {
                if (PointInPolygon(p, h.Points)) return false;
            }
            return true;
        }

        // Ray casting point-in-polygon for simple polygons
        public static bool PointInPolygon(Vector2 p, IReadOnlyList<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var pi = poly[i];
                var pj = poly[j];
                bool intersect = ((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                                 (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / ((pj.Y - pi.Y) + 1e-12f) + pi.X);
                if (intersect)
                    inside = !inside;
            }
            return inside;
        }

        private static bool InBounds(in Vector2 p, in RectD b)
            => p.X >= b.Left && p.X <= b.Left + b.Width &&
               p.Y >= b.Top && p.Y <= b.Top + b.Height;
    }
}
