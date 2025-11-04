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
    // Exports VoronoiResult to DXF format.
    // Extracts unique edges from Voronoi cells to avoid duplicate lines where cells meet.
    // Integration: Home page calls Export to let the user download the generated pattern as DXF.
    public static class DxfExporter
    {
        public static byte[] Export(VoronoiResult result)
        {
            if (result is null)
                throw new ArgumentNullException(nameof(result));

            var dxfFile = new DxfFile();
            dxfFile.Header.Version = DxfAcadVersion.R2013;

            // Extract unique edges from all cells to avoid duplicates
            var uniqueEdges = ExtractUniqueEdges(result.Cells);

            // Add each unique edge as a LINE entity
            foreach (var edge in uniqueEdges)
            {
                var line = new DxfLine(
                    new DxfPoint(edge.Start.X, edge.Start.Y, 0),
                    new DxfPoint(edge.End.X, edge.End.Y, 0)
                );
                dxfFile.Entities.Add(line);
            }

            // Optionally add the boundary as a separate layer
            if (result.Boundary?.Points.Count >= 3)
            {
                var boundaryVertices = new List<DxfLwPolylineVertex>();
                foreach (var point in result.Boundary.Points)
                {
                    boundaryVertices.Add(new DxfLwPolylineVertex
                    {
                        X = point.X,
                        Y = point.Y
                    });
                }

                var boundaryPolyline = new DxfLwPolyline(boundaryVertices)
                {
                    IsClosed = true,
                    Layer = "BOUNDARY"
                };

                dxfFile.Entities.Add(boundaryPolyline);
            }

            // Write DXF to memory stream and return as bytes
            using var ms = new MemoryStream();
            dxfFile.Save(ms);
            return ms.ToArray();
        }

        private static HashSet<Edge> ExtractUniqueEdges(List<Polygon> cells)
        {
            var edges = new HashSet<Edge>();
            const float epsilon = 1e-4f;

            foreach (var cell in cells)
            {
                if (cell.Points.Count < 3)
                    continue;

                // Process each edge of the cell polygon
                for (int i = 0; i < cell.Points.Count; i++)
                {
                    var p1 = cell.Points[i];
                    var p2 = cell.Points[(i + 1) % cell.Points.Count];

                    // Normalize edge direction (smaller point first) to detect duplicates
                    var edge = new Edge(p1, p2, epsilon);
                    edges.Add(edge);
                }
            }

            return edges;
        }

        // Edge struct that normalizes direction and uses value-based equality with tolerance
        private readonly struct Edge : IEquatable<Edge>
        {
            public readonly Vector2 Start;
            public readonly Vector2 End;
            private readonly float _epsilon;

            public Edge(Vector2 a, Vector2 b, float epsilon)
            {
                _epsilon = epsilon;
                // Normalize: put the "smaller" point first (lexicographic order)
                if (IsLess(a, b, epsilon))
                {
                    Start = a;
                    End = b;
                }
                else
                {
                    Start = b;
                    End = a;
                }
            }

            private static bool IsLess(Vector2 a, Vector2 b, float epsilon)
            {
                // Lexicographic comparison: first by X, then by Y
                if (Math.Abs(a.X - b.X) > epsilon)
                    return a.X < b.X;
                return a.Y < b.Y;
            }

            public bool Equals(Edge other)
            {
                return PointsEqual(Start, other.Start, _epsilon) &&
                       PointsEqual(End, other.End, _epsilon);
            }

            public override bool Equals(object? obj)
            {
                return obj is Edge edge && Equals(edge);
            }

            public override int GetHashCode()
            {
                // Simple hash based on rounded coordinates
                int h1 = ((int)(Start.X / _epsilon)).GetHashCode();
                int h2 = ((int)(Start.Y / _epsilon)).GetHashCode();
                int h3 = ((int)(End.X / _epsilon)).GetHashCode();
                int h4 = ((int)(End.Y / _epsilon)).GetHashCode();
                return HashCode.Combine(h1, h2, h3, h4);
            }

            private static bool PointsEqual(Vector2 a, Vector2 b, float epsilon)
            {
                return Math.Abs(a.X - b.X) < epsilon && Math.Abs(a.Y - b.Y) < epsilon;
            }
        }
    }
}
