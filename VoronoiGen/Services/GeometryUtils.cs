using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Clipper2Lib;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    // Geometry helpers using Clipper2 for robust polygon offsetting and conversions.
    public static class GeometryUtils
    {
        // Scale factor for converting float coordinates to integer coordinates for Clipper2
        private const double ScaleFactor = 1000.0;

        // Offset a single polygon by delta. Positive grows the polygon; negative shrinks it.
        // If offsetting inward splits the polygon, returns the largest-area resulting polygon.
        public static Polygon OffsetPolygon(Polygon poly, double delta, JoinType join = JoinType.Miter, EndType endType = EndType.Polygon, double miterLimit = 2.0, double arcTolerance = 0.25)
        {
            var path = ToPath64(poly.Points);
            var co = new ClipperOffset(miterLimit, arcTolerance);
            co.AddPath(path, join, endType);
            var solution = new Paths64();
            co.Execute(delta * ScaleFactor, solution);
            if (solution.Count == 0)
            {
                // Degenerate: return empty polygon
                return new Polygon(new List<Vector2>());
            }

            // Pick largest by absolute area
            var largest = solution
                .OrderByDescending(p => Math.Abs(Clipper.Area(p)))
                .First();
            return new Polygon(ToVector2List(largest));
        }

        public static Path64 ToPath64(IReadOnlyList<Vector2> pts)
        {
            var path = new Path64(pts.Count);
            for (int i = 0; i < pts.Count; i++)
                path.Add(new Point64((long)(pts[i].X * ScaleFactor), (long)(pts[i].Y * ScaleFactor)));
            return path;
        }

        public static List<Vector2> ToVector2List(Path64 path)
        {
            var list = new List<Vector2>(path.Count);
            foreach (var pt in path)
                list.Add(new Vector2((float)(pt.X / ScaleFactor), (float)(pt.Y / ScaleFactor)));
            // ensure no duplicate final point; Clipper doesn't repeat first at end
            return list;
        }
    }
}
