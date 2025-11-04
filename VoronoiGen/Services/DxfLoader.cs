using netDxf;
using netDxf.Entities;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    // Loads DXF file bytes entirely on the client using netDxf and extracts a single outer boundary.
    // Integration: consumed by Home page to get an initial boundary polygon for seed generation and rendering.
    public static class DxfLoader
    {
        // Parse bytes into DxfImport with simplified outer boundary. Holes ignored in MVP (flag kept for future).
        public static DxfImport Load(byte[] data, double chordTolerance = 0.5, double simplifyTolerance = 0.2)
        {
            using var ms = new System.IO.MemoryStream(data);
            var dxf = DxfDocument.Load(ms);
            if (dxf == null) throw new InvalidOperationException("Failed to load DXF");

            // Collect closed polylines and approximations of curves
            var polys = new List<Polygon>();

            // 2d polylines
            foreach (var lw in dxf.Entities.Polylines2D)
            {
                if (lw.IsClosed)
                    polys.Add(ToPolygon(lw));
            }


            // Hatches outer boundaries
            foreach (var hatch in dxf.Entities.Hatches)
            {
                foreach (var path in hatch.BoundaryPaths)
                {
                    var pts = new List<SKPoint>();
                    foreach (var ed in path.Edges)
                    {
                        switch (ed)
                        {
                            case HatchBoundaryPath.Line line:
                                pts.Add(new SKPoint((float)line.Start.X, (float)line.Start.Y));
                                break;
                            case HatchBoundaryPath.Arc arc:
                                pts.AddRange(ApproxArc(arc, chordTolerance));
                                break;
                            case HatchBoundaryPath.Polyline poly:
                                foreach (var v in poly.Vertexes)
                                    pts.Add(new SKPoint((float)v.X, (float)v.Y));
                                break;
                        }
                    }
                    if (pts.Count > 2)
                        polys.Add(new Polygon(CloseIfNeeded(pts)));
                }
            }

            if (polys.Count == 0) throw new InvalidOperationException("No closed boundaries found in DXF");

            // Select the polygon with largest area as outer boundary
            var outer = polys.OrderByDescending(p => p.Area()).First().EnsureCcw();

            // Simplify outer boundary
            outer = new Polygon(DouglasPeucker(outer.Points, simplifyTolerance)).EnsureCcw();

            return new DxfImport(outer, new List<Polygon>(), 1.0);
        }

        private static Polygon ToPolygon(Polyline2D lw)
        {
            var pts = new List<SKPoint>(lw.Vertexes.Count + 1);
            foreach (var v in lw.Vertexes)
            {
                // MVP: ignore bulge arcs; netDxf provides Bulge on vertex; straight approximation suffices
                pts.Add(new SKPoint((float)v.Position.X, (float)v.Position.Y));
            }
            return new Polygon(CloseIfNeeded(pts));
        }


        private static List<SKPoint> CloseIfNeeded(List<SKPoint> pts)
        {
            if (pts.Count == 0) return pts;
            var first = pts[0];
            var last = pts[^1];
            if (SKPoint.Distance(first, last) > 1e-4)
                pts.Add(first);
            return pts;
        }

        private static IEnumerable<SKPoint> ApproxArc(HatchBoundaryPath.Arc arc, double chordTolerance)
        {
            // MVP: sample arc at equal steps based on chord tolerance
            var pts = new List<SKPoint>();
            double start = arc.StartAngle * Math.PI / 180.0;
            double end = arc.EndAngle * Math.PI / 180.0;
            double sweep = end - start;
            if (sweep <= 0) sweep += 2 * Math.PI;
            var r = arc.Radius;
            int steps = Math.Max(3, (int)Math.Ceiling(sweep * r / Math.Max(1e-6, chordTolerance)));
            for (int i = 0; i <= steps; i++)
            {
                double t = start + sweep * i / steps;
                pts.Add(new SKPoint((float)(arc.Center.X + r * Math.Cos(t)), (float)(arc.Center.Y + r * Math.Sin(t))));
            }
            return pts;
        }

        // Douglas-Peucker line simplification (treats the polyline as open; acceptable for small tolerances)
        private static List<SKPoint> DouglasPeucker(IReadOnlyList<SKPoint> points, double tolerance)
        {
            if (points.Count < 3) return points.ToList();

            int index = -1;
            double maxDist = 0;
            var start = points[0];
            var end = points[^1];

            for (int i = 1; i < points.Count - 1; i++)
            {
                double d = PerpendicularDistance(points[i], start, end);
                if (d > maxDist)
                {
                    index = i;
                    maxDist = d;
                }
            }

            if (maxDist > tolerance)
            {
                var left = DouglasPeucker(points.Take(index + 1).ToList(), tolerance);
                var right = DouglasPeucker(points.Skip(index).ToList(), tolerance);
                var result = new List<SKPoint>(left);
                result.AddRange(right.Skip(1));
                return result;
            }
            else
            {
                return new List<SKPoint> { start, end };
            }
        }

        private static double PerpendicularDistance(SKPoint p, SKPoint a, SKPoint b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            if (Math.Abs(dx) < 1e-8 && Math.Abs(dy) < 1e-8)
                return System.Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
            double projX = a.X + t * dx;
            double projY = a.Y + t * dy;
            return System.Math.Sqrt((p.X - projX) * (p.X - projX) + (p.Y - projY) * (p.Y - projY));
        }
    }
}
