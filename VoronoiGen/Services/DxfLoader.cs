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
    // - Optionally stitches connected LINE entities into closed contours
    // - Approximates bulge arcs using a chord tolerance
    // - Handles SPLINE, ARC, and ELLIPSE entities with high fidelity
    // - Picks the largest-area loop as outer boundary
    // - Classifies all other loops whose centroid lies inside the outer as holes
    // - Optionally simplifies with Douglas–Peucker
    public static class DxfLoader
    {
        public static DxfImport Load(byte[] bytes, double chordTolerance = 0.1, double simplifyTolerance = 0.0, bool closeLineContours = false)
        {
            if (bytes is null || bytes.Length == 0)
                throw new ArgumentException("DXF bytes are empty");

            using var ms = new MemoryStream(bytes);
            var dxf = DxfFile.Load(ms);

            // Unit scale: we keep model space units by default (1.0). Header units can be used if needed.
            double unitScale = GetUnitScale(dxf);

            // Auto-derive a base tolerance from the DXF extents if requested (<= 0 triggers auto).
            if (chordTolerance <= 0)
            {
                chordTolerance = ComputeAutoChordTolerance(dxf);
            }

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

            // SPLINE (closed splines become rings)
            foreach (var sp in dxf.Entities.OfType<DxfSpline>())
            {
                if (!sp.IsClosed) continue;
                var pts = ApproximateSpline(sp, chordTolerance);
                if (pts.Count >= 3)
                    rings.Add(pts);
            }

            // ELLIPSE (handle full ellipses and elliptical arcs)
            foreach (var el in dxf.Entities.OfType<DxfEllipse>())
            {
                var pts = ApproximateEllipse(el, chordTolerance);
                if (pts.Count >= 3)
                    rings.Add(pts);
            }

            // ARC (open arcs won't form closed loops by themselves, but collect them anyway)
            foreach (var arc in dxf.Entities.OfType<DxfArc>())
            {
                var pts = ApproximateArc(arc, chordTolerance);
                if (pts.Count >= 2)
                {
                    // If arc is nearly a full circle (within tolerance), close it
                    if (IsNearlyFullCircle(arc.StartAngle, arc.EndAngle))
                    {
                        pts = NormalizeClosed(pts);
                        if (pts.Count >= 3)
                            rings.Add(pts);
                    }
                }
            }

            if (closeLineContours)
            {
                rings.AddRange(BuildClosedLineContours(dxf.Entities.OfType<DxfLine>(), chordTolerance));
            }

            if (rings.Count == 0)
                throw new InvalidOperationException("No closed outlines found in DXF. Expected closed LWPOLYLINE/POLYLINE, SPLINE, CIRCLE, ELLIPSE, ARC, or connected LINE contours when line-contour closing is enabled.");

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

        private static List<List<Vector2>> BuildClosedLineContours(IEnumerable<DxfLine> lines, double tolerance)
        {
            var joinTolerance = Math.Max(tolerance, 1e-6);
            var unused = lines
                .Select(l => new LineSegment(
                    new Vector2((float)l.P1.X, (float)l.P1.Y),
                    new Vector2((float)l.P2.X, (float)l.P2.Y)))
                .Where(s => IsFinite(s.Start) && IsFinite(s.End) && Distance(s.Start, s.End) > joinTolerance)
                .ToList();

            var rings = new List<List<Vector2>>();

            while (unused.Count > 0)
            {
                var current = unused[^1];
                unused.RemoveAt(unused.Count - 1);

                var contour = new List<Vector2> { current.Start, current.End };
                var extended = true;

                while (extended)
                {
                    extended = TryAppendConnectedSegment(contour, unused, joinTolerance)
                        || TryPrependConnectedSegment(contour, unused, joinTolerance);
                }

                if (contour.Count >= 3 && Distance(contour[0], contour[^1]) <= joinTolerance)
                {
                    var normalized = NormalizeClosed(contour);
                    if (normalized.Count >= 3 && Math.Abs(SignedArea(normalized)) > joinTolerance * joinTolerance)
                    {
                        rings.Add(normalized);
                    }
                }
            }

            return rings;
        }

        private static bool TryAppendConnectedSegment(List<Vector2> contour, List<LineSegment> unused, double tolerance)
        {
            var end = contour[^1];
            for (int i = unused.Count - 1; i >= 0; i--)
            {
                var segment = unused[i];
                if (Distance(end, segment.Start) <= tolerance)
                {
                    contour.Add(segment.End);
                    unused.RemoveAt(i);
                    return true;
                }

                if (Distance(end, segment.End) <= tolerance)
                {
                    contour.Add(segment.Start);
                    unused.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        private static bool TryPrependConnectedSegment(List<Vector2> contour, List<LineSegment> unused, double tolerance)
        {
            var start = contour[0];
            for (int i = unused.Count - 1; i >= 0; i--)
            {
                var segment = unused[i];
                if (Distance(start, segment.End) <= tolerance)
                {
                    contour.Insert(0, segment.Start);
                    unused.RemoveAt(i);
                    return true;
                }

                if (Distance(start, segment.Start) <= tolerance)
                {
                    contour.Insert(0, segment.End);
                    unused.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        private static double SignedArea(IReadOnlyList<Vector2> pts)
        {
            double area = 0;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                area += (double)(pts[j].X * pts[i].Y - pts[i].X * pts[j].Y);
            }

            return area * 0.5;
        }

        private static double Distance(Vector2 a, Vector2 b) => Vector2.Distance(a, b);

        private static bool IsFinite(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y);

        private readonly record struct LineSegment(Vector2 Start, Vector2 End);

        private static List<Vector2> ApproximateCircle(DxfCircle c, double chordTolerance)
        {
            var center = new Vector2((float)c.Center.X, (float)c.Center.Y);
            var r = (float)c.Radius;
            if (r <= 0) return new List<Vector2>();

            // Choose number of segments from tolerance (use shared estimator for consistency)
            int segments = EstimateSegmentsFromTolerance(r, 2.0 * Math.PI, chordTolerance);
            segments = Math.Max(8, segments);            // ensure decent base smoothness
            segments = Math.Min(2048, segments);         // avoid runaway tesselation

            var pts = new List<Vector2>(segments);
            for (int i = 0; i < segments; i++)
            {
                double a = i * (2.0 * Math.PI / segments);
                pts.Add(new Vector2(center.X + (float)(r * Math.Cos(a)), center.Y + (float)(r * Math.Sin(a))));
            }
            return NormalizeClosed(pts);
        }

        private static List<Vector2> ApproximateSpline(DxfSpline spline, double chordTolerance)
        {
            // B-spline evaluation based on control points and knot vector
            // Reference: https://pages.mtu.edu/~shene/COURSES/cs3621/NOTES/spline/B-spline/bspline-curve.html

            if (spline.ControlPoints.Count < 2)
                return new List<Vector2>();

            // If fit points are provided and control points aren't, use fit points
            if (spline.FitPoints.Count > 0 && spline.ControlPoints.Count == 0)
            {
                return spline.FitPoints
                    .Select(p => new Vector2((float)p.X, (float)p.Y))
                    .ToList();
            }

            var controlPoints = spline.ControlPoints
                .Select(cp => new Vector2((float)cp.Point.X, (float)cp.Point.Y))
                .ToList();

            var weights = spline.ControlPoints
                .Select(cp => cp.Weight)
                .ToList();

            var knots = spline.KnotValues.ToList();
            int degree = spline.DegreeOfCurve;

            // Validate knot vector
            if (knots.Count == 0)
            {
                // Generate uniform knot vector if not provided
                knots = GenerateUniformKnotVector(controlPoints.Count, degree);
            }

            // Estimate number of sample points based on chord tolerance
            int numSamples = Math.Max(controlPoints.Count * 8, (int)(controlPoints.Count / Math.Max(chordTolerance, 0.01)));
            numSamples = Math.Clamp(numSamples, 32, 1000); // Cap at [32..1000] points

            var result = new List<Vector2>(numSamples);

            // Parameter range (typically from knot[degree] to knot[n])
            double tStart = knots[degree];
            double tEnd = knots[knots.Count - degree - 1];

            for (int i = 0; i <= numSamples; i++)
            {
                double t = tStart + (tEnd - tStart) * (i / (double)numSamples);

                // Clamp t to valid range
                t = Math.Clamp(t, tStart, tEnd);

                var point = EvaluateBSpline(controlPoints, weights, knots, degree, t, spline.IsRational);

                // Apply chord tolerance filtering
                if (result.Count == 0 || Vector2.Distance(result[^1], point) >= chordTolerance * 0.5)
                {
                    result.Add(point);
                }
            }

            // Ensure last point is included
            if (result.Count > 0)
            {
                var lastPoint = EvaluateBSpline(controlPoints, weights, knots, degree, tEnd, spline.IsRational);
                if (Vector2.Distance(result[^1], lastPoint) >= 1e-6f)
                {
                    result.Add(lastPoint);
                }
            }

            return result;
        }

        private static List<double> GenerateUniformKnotVector(int numControlPoints, int degree)
        {
            int numKnots = numControlPoints + degree + 1;
            var knots = new List<double>(numKnots);

            for (int i = 0; i < numKnots; i++)
            {
                knots.Add(i);
            }

            return knots;
        }

        private static Vector2 EvaluateBSpline(List<Vector2> controlPoints, List<double> weights,
            List<double> knots, int degree, double t, bool isRational)
        {
            int n = controlPoints.Count - 1;

            if (isRational)
            {
                // NURBS evaluation (rational B-spline)
                var point = Vector2.Zero;
                double weightSum = 0.0;

                for (int i = 0; i <= n; i++)
                {
                    double basis = BSplineBasis(i, degree, t, knots);
                    double w = weights[i];
                    point += controlPoints[i] * (float)(basis * w);
                    weightSum += basis * w;
                }

                return weightSum > 1e-10 ? point / (float)weightSum : point;
            }
            else
            {
                // Non-rational B-spline
                var point = Vector2.Zero;

                for (int i = 0; i <= n; i++)
                {
                    double basis = BSplineBasis(i, degree, t, knots);
                    point += controlPoints[i] * (float)basis;
                }

                return point;
            }
        }

        private static double BSplineBasis(int i, int p, double t, List<double> knots)
        {
            // Cox-de Boor recursion formula for B-spline basis functions
            if (p == 0)
            {
                return (t >= knots[i] && t < knots[i + 1]) ? 1.0 : 0.0;
            }

            double denom1 = knots[i + p] - knots[i];
            double denom2 = knots[i + p + 1] - knots[i + 1];

            double term1 = 0.0;
            if (Math.Abs(denom1) > 1e-10)
            {
                term1 = ((t - knots[i]) / denom1) * BSplineBasis(i, p - 1, t, knots);
            }

            double term2 = 0.0;
            if (Math.Abs(denom2) > 1e-10)
            {
                term2 = ((knots[i + p + 1] - t) / denom2) * BSplineBasis(i + 1, p - 1, t, knots);
            }

            return term1 + term2;
        }

        private static List<Vector2> ApproximateEllipse(DxfEllipse ellipse, double chordTolerance)
        {
            var center = new Vector2((float)ellipse.Center.X, (float)ellipse.Center.Y);
            var majorAxis = new Vector2((float)ellipse.MajorAxis.X, (float)ellipse.MajorAxis.Y);
            double minorAxisRatio = ellipse.MinorAxisRatio;
            double startParam = ellipse.StartParameter;
            double endParam = ellipse.EndParameter;

            // Calculate semi-major and semi-minor axes
            double semiMajor = majorAxis.Length();
            double semiMinor = semiMajor * minorAxisRatio;

            // Major axis angle
            double majorAngle = Math.Atan2(majorAxis.Y, majorAxis.X);

            // Determine angular span
            double angularSpan = endParam - startParam;
            if (angularSpan < 0)
                angularSpan += 2.0 * Math.PI;

            // Estimate segments based on chord tolerance and the larger axis
            double maxRadius = Math.Max(semiMajor, semiMinor);
            int segments = EstimateSegmentsFromTolerance(maxRadius, angularSpan, chordTolerance);
            segments = Math.Max(16, segments);
            segments = Math.Min(2048, segments);

            var pts = new List<Vector2>(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                double t = startParam + angularSpan * (i / (double)segments);

                // Parametric ellipse equation
                double x = semiMajor * Math.Cos(t);
                double y = semiMinor * Math.Sin(t);

                // Rotate by major axis angle
                double xRot = x * Math.Cos(majorAngle) - y * Math.Sin(majorAngle);
                double yRot = x * Math.Sin(majorAngle) + y * Math.Cos(majorAngle);

                pts.Add(new Vector2(
                    center.X + (float)xRot,
                    center.Y + (float)yRot
                ));
            }

            return pts;
        }

        private static List<Vector2> ApproximateArc(DxfArc arc, double chordTolerance)
        {
            var center = new Vector2((float)arc.Center.X, (float)arc.Center.Y);
            double radius = arc.Radius;
            double startAngle = arc.StartAngle * Math.PI / 180.0; // Convert to radians
            double endAngle = arc.EndAngle * Math.PI / 180.0;

            // Normalize angles and calculate sweep
            double sweep = endAngle - startAngle;
            if (sweep < 0)
                sweep += 2.0 * Math.PI;

            int segments = EstimateSegmentsFromTolerance(radius, sweep, chordTolerance);
            segments = Math.Max(2, segments);
            segments = Math.Min(2048, segments);

            var pts = new List<Vector2>(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                double angle = startAngle + sweep * (i / (double)segments);
                pts.Add(new Vector2(
                    center.X + (float)(radius * Math.Cos(angle)),
                    center.Y + (float)(radius * Math.Sin(angle))
                ));
            }

            return pts;
        }

        private static bool IsNearlyFullCircle(double startAngleDeg, double endAngleDeg)
        {
            double diff = Math.Abs(endAngleDeg - startAngleDeg);
            // Check if the arc spans approximately 360 degrees (within 1 degree tolerance)
            return Math.Abs(diff - 360.0) < 1.0 || diff < 1.0;
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
            segments = Math.Min(2048, segments);

            var pts = new List<Vector2>(segments + 1) { p0 };
            for (int i = 1; i < segments; i++)
            {
                double a = a0 + sweep * (i / (double)segments);
                pts.Add(new Vector2((float)(center.X + r * Math.Cos(a)), (float)(center.Y + r * Math.Sin(a))));
            }
            pts.Add(p1);
            return pts;
        }

        // Intelligent segment estimator: uses global chordTolerance, but tightens per-entity to <= 0.5% of radius.
        private static int EstimateSegmentsFromTolerance(double radius, double angleAbs, double chordTolerance)
        {
            // Fallback if no tolerance has been set
            if (chordTolerance <= 0 || double.IsNaN(chordTolerance))
            {
                return Math.Max(1, (int)Math.Ceiling(angleAbs / (Math.PI / 12.0))); // ~15-degree default
            }

            // Make small-radius entities smooth by bounding sagitta to a fraction of radius
            double relativeSagitta = radius > 0 ? radius * 0.005 : chordTolerance; // 0.5% of radius
            double effectiveTol = Math.Min(chordTolerance, relativeSagitta);

            // Convert sagitta to max angle per segment
            double cosArg = 1.0 - effectiveTol / Math.Max(1e-9, radius);
            cosArg = Math.Clamp(cosArg, -1.0, 1.0);
            double deltaMax = 2.0 * Math.Acos(cosArg);

            // Guard for degenerate cases
            if (double.IsNaN(deltaMax) || deltaMax <= 0)
            {
                deltaMax = Math.PI / 12.0; // ~15 degrees
            }

            int segments = Math.Max(1, (int)Math.Ceiling(angleAbs / deltaMax));
            segments = Math.Clamp(segments, 1, 4096);
            return segments;
        }

        private static double NormalizeAngleSigned(double a)
        {
            while (a <= -Math.PI) a += 2 * Math.PI;
            while (a > Math.PI) a -= 2 * Math.PI;
            return a;
        }

        private static double NormalizeAnglePositive(double a)
        {
            while (a < 0) a += 2 * Math.PI;
            while (a >= 2 * Math.PI) a -= 2 * Math.PI;
            return a;
        }

        private static bool AngleWithinSweep(double start, double end, double angle)
        {
            // assumes end >= start in [0,2π)
            if (end < start) end += 2 * Math.PI;
            double ang = NormalizeAnglePositive(angle);
            if (ang < start) ang += 2 * Math.PI;
            return ang >= start - 1e-12 && ang <= end + 1e-12;
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

        // --- Auto chord tolerance based on DXF extents ---

        private static double ComputeAutoChordTolerance(DxfFile dxf)
        {
            var (min, max) = ComputeDxfExtents(dxf);

            double dx = max.X - min.X;
            double dy = max.Y - min.Y;
            double diag = Math.Sqrt(dx * dx + dy * dy);

            if (double.IsNaN(diag) || diag <= 0)
                return 0.1; // safe fallback in model units

            // Base tolerance: ~0.1% of the drawing diagonal, clamped to a reasonable band
            double tol = diag * 0.001;              // 0.1%
            double tolMin = diag * 0.00001;         // 0.001%
            double tolMax = diag * 0.01;            // 1%

            tol = Math.Clamp(tol, tolMin, tolMax);
            return tol;
        }

        private static (Vector2 min, Vector2 max) ComputeDxfExtents(DxfFile dxf)
        {
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);

            void Include(Vector2 p)
            {
                if (float.IsFinite(p.X) && float.IsFinite(p.Y))
                {
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }
            }

            // LWPOLYLINE vertices
            foreach (var lw in dxf.Entities.OfType<DxfLwPolyline>())
            {
                foreach (var v in lw.Vertices)
                    Include(new Vector2((float)v.X, (float)v.Y));
            }

            // POLYLINE vertices
            foreach (var pl in dxf.Entities.OfType<DxfPolyline>())
            {
                foreach (var v in pl.Vertices)
                    Include(new Vector2((float)v.Location.X, (float)v.Location.Y));
            }

            // CIRCLE extents
            foreach (var c in dxf.Entities.OfType<DxfCircle>())
            {
                var center = new Vector2((float)c.Center.X, (float)c.Center.Y);
                float r = (float)c.Radius;
                Include(center + new Vector2(+r, 0));
                Include(center + new Vector2(-r, 0));
                Include(center + new Vector2(0, +r));
                Include(center + new Vector2(0, -r));
            }

            // ARC extents (start, end, and cardinal angles within sweep)
            foreach (var a in dxf.Entities.OfType<DxfArc>())
            {
                var center = new Vector2((float)a.Center.X, (float)a.Center.Y);
                double r = a.Radius;
                double s = a.StartAngle * Math.PI / 180.0;
                double e = a.EndAngle * Math.PI / 180.0;

                double sweep = e - s;
                if (sweep < 0) sweep += 2 * Math.PI;

                // start & end
                Include(new Vector2((float)(center.X + r * Math.Cos(s)), (float)(center.Y + r * Math.Sin(s))));
                Include(new Vector2((float)(center.X + r * Math.Cos(e)), (float)(center.Y + r * Math.Sin(e))));

                // cardinals
                var card = new[] { 0.0, Math.PI * 0.5, Math.PI, Math.PI * 1.5 };
                foreach (var ang in card)
                {
                    double sN = NormalizeAnglePositive(s);
                    double eN = NormalizeAnglePositive(e);
                    if (AngleWithinSweep(sN, eN, ang))
                    {
                        Include(new Vector2((float)(center.X + r * Math.Cos(ang)), (float)(center.Y + r * Math.Sin(ang))));
                    }
                }
            }

            // ELLIPSE: sample a reasonable number of points for extents (fast and robust)
            foreach (var el in dxf.Entities.OfType<DxfEllipse>())
            {
                var center = new Vector2((float)el.Center.X, (float)el.Center.Y);
                var majorAxis = new Vector2((float)el.MajorAxis.X, (float)el.MajorAxis.Y);
                double a = majorAxis.Length();
                double b = a * el.MinorAxisRatio;
                double phi = Math.Atan2(majorAxis.Y, majorAxis.X);

                double start = el.StartParameter;
                double end = el.EndParameter;
                double span = end - start;
                if (span < 0) span += 2 * Math.PI;

                int steps = 72;
                for (int i = 0; i <= steps; i++)
                {
                    double t = start + span * (i / (double)steps);
                    double x = a * Math.Cos(t);
                    double y = b * Math.Sin(t);
                    double xr = x * Math.Cos(phi) - y * Math.Sin(phi);
                    double yr = x * Math.Sin(phi) + y * Math.Cos(phi);
                    Include(new Vector2(center.X + (float)xr, center.Y + (float)yr));
                }
            }

            // SPLINE: use provided fit points or control points for coarse extents
            foreach (var sp in dxf.Entities.OfType<DxfSpline>())
            {
                if (sp.FitPoints.Count > 0)
                {
                    foreach (var p in sp.FitPoints)
                        Include(new Vector2((float)p.X, (float)p.Y));
                }
                else
                {
                    foreach (var cp in sp.ControlPoints)
                        Include(new Vector2((float)cp.Point.X, (float)cp.Point.Y));
                }
            }

            if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) ||
                !float.IsFinite(max.X) || !float.IsFinite(max.Y))
            {
                // Fallback to a tiny box if nothing was found
                min = new Vector2(0, 0);
                max = new Vector2(1, 1);
            }

            return (min, max);
        }
    }
}
