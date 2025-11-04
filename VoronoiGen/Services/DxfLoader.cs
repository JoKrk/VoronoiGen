using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VoronoiGen.Models;
using IxMilia.Dxf;
using IxMilia.Dxf.Entities;

namespace VoronoiGen.Services
{
    // Loads DXF file bytes entirely on the client and extracts an outer boundary with optional inner holes.
    // - Uses IxMilia.Dxf to parse entities
    // - Collects closed LWPOLYLINE and POLYLINE (2D) loops and Circles
    // - Approximates bulge arcs using a chord tolerance
    // - Picks the largest-area loop as outer boundary
    // - Classifies all other loops whose centroid lies inside the outer as holes
    // - Optionally simplifies with Douglas–Peucker
    public static class DxfLoader
    {
        public static DxfImport Load(byte[] bytes, double chordTolerance = 0.5, double simplifyTolerance = 0.0)
        {
            if (bytes is null || bytes.Length == 0)
                throw new ArgumentException("DXF bytes are empty");

            using var ms = new MemoryStream(bytes);
            var dxf = DxfFile.Load(ms);

            // Unit scale: we keep model space units by default (1.0). Header units can be used if needed.
            double unitScale = GetUnitScale(dxf);

            var rings = new List<List<Vector2>>();

            // LWPOLYLINE
            foreach (var lw in dxf.Entities.OfType<DxfLwPolyline>())
            {
                if (!lw.IsClosed || lw.Vertices.Count < 3) continue;
                var pts = ApproximateLwPolyline(lw, chordTolerance);
                if (pts.Count >= 3)
                    rings.Add(pts);
            }

            // POLYLINE (2D vertices). We project to XY and ignore Z.
            foreach (var pl in dxf.Entities.OfType<DxfPolyline>())
            {
                if (!pl.IsClosed || pl.Vertices.Count < 3) continue;
                var vertices2D = pl.Vertices
                    .Select(v => new Vector2((float)v.Location.X, (float)v.Location.Y))
                    .ToList();
                if (vertices2D.Count >= 3)
                    rings.Add(NormalizeClosed(vertices2D));
            }

            // CIRCLE (closed loop)
            foreach (var c in dxf.Entities.OfType<DxfCircle>())
            {
                var pts = ApproximateCircle(c, chordTolerance);
                if (pts.Count >= 3)
                    rings.Add(pts);
            }

            if (rings.Count == 0)
                throw new InvalidOperationException("No closed outlines found in DXF. Expected closed LWPOLYLINE/POLYLINE or circle.");

            // Convert to Polygon, ensure no duplicate last point and orient CCW for now (we'll orient holes later)
            var polys = rings
                .Select(r => new Polygon(NormalizeClosed(r)))
                .Select(p => p.EnsureCcw())
                .ToList();

            // Simplify (optional)
            if (simplifyTolerance > 0)
            {
                polys = polys
                    .Select(p => new Polygon(DouglasPeuckerClosed(p.Points, (float)simplifyTolerance)))
                    .Select(p => p.EnsureCcw())
                    .ToList();
            }

            // Pick the largest area as the outer boundary
            var outer = polys.OrderByDescending(p => p.Area()).First();

            // Classify holes: rings whose centroid lies inside 'outer'.
            var holes = new List<Polygon>();
            foreach (var p in polys)
            {
                if (ReferenceEquals(p, outer)) continue;
                if (p.Points.Count < 3) continue;
                var c = p.Centroid();
                if (SeedGenerator.PointInPolygon(c, outer.Points))
                {
                    // Ensure hole orientation is CW for conventional polygon-with-holes
                    var hole = p.SignedArea() > 0 ? new Polygon(new List<Vector2>(p.Points.AsEnumerable().Reverse())) : p;
                    holes.Add(hole);
                }
            }

            return new DxfImport(outer, holes, unitScale);
        }

        private static double GetUnitScale(DxfFile dxf)
        {
            // Keep unit scale as 1.0; if desired, you can map header units to millimeters here.
            // var u = dxf.Header.InsUnits; // available in IxMilia.Dxf as DxfUnits if needed
            return 1.0;
        }

        private static List<Vector2> ApproximateLwPolyline(DxfLwPolyline lw, double chordTolerance)
        {
            var result = new List<Vector2>();
            int n = lw.Vertices.Count;
            if (n == 0) return result;

            for (int i = 0; i < n; i++)
            {
                var v0 = lw.Vertices[i];
                var v1 = lw.Vertices[(i + 1) % n];
                var p0 = new Vector2((float)v0.X, (float)v0.Y);
                var p1 = new Vector2((float)v1.X, (float)v1.Y);
                double bulge = v0.Bulge;

                if (i == 0)
                    result.Add(p0);

                if (Math.Abs(bulge) < 1e-12)
                {
                    // straight segment
                    result.Add(p1);
                }
                else
                {
                    // arc segment from p0 -> p1
                    var arcPts = ApproximateBulgeArc(p0, p1, bulge, chordTolerance);
                    // arcPts contains p0..p1; we already added p0
                    for (int k = 1; k < arcPts.Count; k++)
                        result.Add(arcPts[k]);
                }
            }

            // Remove duplicate last point if equal to first
            return NormalizeClosed(result);
        }

        private static List<Vector2> ApproximateCircle(DxfCircle c, double chordTolerance)
        {
            var center = new Vector2((float)c.Center.X, (float)c.Center.Y);
            var r = (float)c.Radius;
            if (r <= 0) return new List<Vector2>();

            // Choose number of segments from tolerance
            int segments = 64;
            if (chordTolerance > 0)
            {
                // For a circle segmented into N, delta = 2*acos(1 - tol/r)
                double cosArg = 1.0 - chordTolerance / Math.Max(1e-9, r);
                cosArg = Math.Clamp(cosArg, -1.0, 1.0);
                double deltaMax = 2.0 * Math.Acos(cosArg);
                if (deltaMax > 0)
                    segments = Math.Max(8, (int)Math.Ceiling(2.0 * Math.PI / deltaMax));
            }

            var pts = new List<Vector2>(segments);
            for (int i = 0; i < segments; i++)
            {
                double a = i * (2.0 * Math.PI / segments);
                pts.Add(new Vector2(center.X + (float)(r * Math.Cos(a)), center.Y + (float)(r * Math.Sin(a))));
            }
            return NormalizeClosed(pts);
        }

        private static List<Vector2> ApproximateBulgeArc(Vector2 p0, Vector2 p1, double bulge, double chordTolerance)
        {
            // bulge = tan(theta/4), theta signed sweep from p0 to p1
            double theta = 4.0 * Math.Atan(bulge);
            double vx = p1.X - p0.X, vy = p1.Y - p0.Y;
            double c = Math.Sqrt(vx * vx + vy * vy);
            if (c <= 1e-9 || Math.Abs(theta) <= 1e-9)
                return new List<Vector2> { p0, p1 };

            var m = new Vector2((p0.X + p1.X) * 0.5f, (p0.Y + p1.Y) * 0.5f);
            // Unit left normal
            double nx = -vy / c, ny = vx / c;
            // distance from midpoint to center along left normal
            double d = (c / 2.0) / Math.Tan(theta / 2.0);
            var center = new Vector2((float)(m.X + nx * d), (float)(m.Y + ny * d));

            double dx0 = p0.X - center.X, dy0 = p0.Y - center.Y;
            double r = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            double a0 = Math.Atan2(dy0, dx0);
            double a1 = Math.Atan2(p1.Y - center.Y, p1.X - center.X);

            // Ensure sweep direction matches theta sign
            double sweep = NormalizeAngleSigned(a1 - a0);
            if (theta > 0 && sweep < 0) sweep += 2 * Math.PI;
            if (theta < 0 && sweep > 0) sweep -= 2 * Math.PI;

            int segments = Math.Max(1, EstimateSegmentsFromTolerance(r, Math.Abs(sweep), chordTolerance));

            var pts = new List<Vector2>(segments + 1) { p0 };
            for (int i = 1; i < segments; i++)
            {
                double a = a0 + sweep * (i / (double)segments);
                pts.Add(new Vector2((float)(center.X + r * Math.Cos(a)), (float)(center.Y + r * Math.Sin(a))));
            }
            pts.Add(p1);
            return pts;
        }

        private static int EstimateSegmentsFromTolerance(double radius, double angleAbs, double chordTolerance)
        {
            if (chordTolerance <= 0)
            {
                // default to ~15-degree steps
                return Math.Max(1, (int)Math.Ceiling(angleAbs / (Math.PI / 12.0)));
            }
            double cosArg = 1.0 - chordTolerance / Math.Max(1e-9, radius);
            cosArg = Math.Clamp(cosArg, -1.0, 1.0);
            double deltaMax = 2.0 * Math.Acos(cosArg);
            if (deltaMax <= 0 || double.IsNaN(deltaMax))
                return Math.Max(1, (int)Math.Ceiling(angleAbs / (Math.PI / 12.0)));
            return Math.Max(1, (int)Math.Ceiling(angleAbs / deltaMax));
        }

        private static double NormalizeAngleSigned(double a)
        {
            while (a <= -Math.PI) a += 2 * Math.PI;
            while (a > Math.PI) a -= 2 * Math.PI;
            return a;
        }

        private static List<Vector2> NormalizeClosed(List<Vector2> pts)
        {
            if (pts.Count == 0) return pts;
            // Remove duplicate last point if it equals first within epsilon
            var first = pts[0];
            var last = pts[^1];
            if (Vector2.Distance(first, last) < 1e-6f)
                pts.RemoveAt(pts.Count - 1);
            return pts;
        }

        // Douglas–Peucker for closed polygons (returns no duplicate end point)
        private static List<Vector2> DouglasPeuckerClosed(IReadOnlyList<Vector2> pts, float epsilon)
        {
            if (pts.Count < 3 || epsilon <= 0) return new List<Vector2>(pts);
            // Work on a copy with explicit closure
            var work = new List<Vector2>(pts);
            work.Add(pts[0]);
            var simplified = DouglasPeuckerOpen(work, epsilon);
            // Remove the duplicate last
            if (simplified.Count > 1)
                simplified.RemoveAt(simplified.Count - 1);
            return simplified;
        }

        // Standard Douglas–Peucker for open polylines
        private static List<Vector2> DouglasPeuckerOpen(IReadOnlyList<Vector2> pts, float epsilon)
        {
            if (pts.Count < 3) return new List<Vector2>(pts);

            int index = -1;
            float dmax = 0f;
            for (int i = 1; i < pts.Count - 1; i++)
            {
                float d = PerpendicularDistance(pts[i], pts[0], pts[^1]);
                if (d > dmax)
                {
                    index = i;
                    dmax = d;
                }
            }

            if (dmax > epsilon)
            {
                var rec1 = DouglasPeuckerOpen(pts.Take(index + 1).ToList(), epsilon);
                var rec2 = DouglasPeuckerOpen(pts.Skip(index).ToList(), epsilon);
                // merge, avoiding duplicate at the joint
                rec1.RemoveAt(rec1.Count - 1);
                rec1.AddRange(rec2);
                return rec1;
            }
            else
            {
                return new List<Vector2> { pts[0], pts[^1] };
            }
        }

        private static float PerpendicularDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var vx = b - a;
            var wx = p - a;

            float c1 = Vector2.Dot(vx, wx);
            if (c1 <= 0) return Vector2.Distance(p, a);

            float c2 = Vector2.Dot(vx, vx);
            if (c2 <= c1) return Vector2.Distance(p, b);

            float t = c1 / c2;
            var proj = a + t * vx;
            return Vector2.Distance(p, proj);
        }
    }
}