using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using IxMilia.Dxf.Objects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    // Exports VoronoiResult to DXF format with separate layers for cells, holes, and boundary.
    // Integration: Home page calls Export to let the user download the generated pattern as DXF.
    public static class DxfExporter
    {
        public static byte[] ExportPolylines(VoronoiResult result, List<Polygon>? holes = null)
        {
            if (result is null)
                throw new ArgumentNullException(nameof(result));

            var dxfFile = new DxfFile();
            dxfFile.Header.Version = DxfAcadVersion.R2013;

            // Define layers
            dxfFile.Layers.Add(new DxfLayer("CELLS") { Color = DxfColor.FromIndex(7) }); // White
            dxfFile.Layers.Add(new DxfLayer("BOUNDARY") { Color = DxfColor.FromIndex(1) }); // Red
            dxfFile.Layers.Add(new DxfLayer("HOLES") { Color = DxfColor.FromIndex(5) }); // Blue

            // Layer 1: Add individual Voronoi cells as closed polylines
            foreach (var cell in result.Cells)
            {
                if (cell.Points.Count < 3)
                    continue;

                var cellVertices = new List<DxfLwPolylineVertex>();
                foreach (var point in cell.Points)
                {
                    cellVertices.Add(new DxfLwPolylineVertex
                    {
                        X = point.X,
                        Y = point.Y
                    });
                }

                var cellPolyline = new DxfLwPolyline(cellVertices)
                {
                    IsClosed = true,
                    Layer = "CELLS"
                };

                dxfFile.Entities.Add(cellPolyline);
            }

            // Layer 2: Add the outer boundary
            if (result.OriginalBoundary?.Points.Count >= 3)
            {
                var boundaryVertices = new List<DxfLwPolylineVertex>();
                foreach (var point in result.OriginalBoundary.Points)
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

            // Layer 3: Add internal holes
            if (holes != null)
            {
                foreach (var hole in holes)
                {
                    if (hole.Points.Count < 3)
                        continue;

                    var holeVertices = new List<DxfLwPolylineVertex>();
                    foreach (var point in hole.Points)
                    {
                        holeVertices.Add(new DxfLwPolylineVertex
                        {
                            X = point.X,
                            Y = point.Y
                        });
                    }

                    var holePolyline = new DxfLwPolyline(holeVertices)
                    {
                        IsClosed = true,
                        Layer = "HOLES"
                    };

                    dxfFile.Entities.Add(holePolyline);
                }
            }

            // Write DXF to memory stream and return as bytes
            using var ms = new MemoryStream();
            dxfFile.Save(ms);
            return ms.ToArray();
        }

        /// <param name="result">Voronoi result containing cells.</param>
        /// <param name="holes">Optional hole polygons.</param>
        /// <param name="tension">Catmull–Rom tension in [0,1].</param>
        /// <param name="samplesPerBezier">Samples per cubic segment (>=2 recommended).</param>
        public static byte[] ExportSplines(
            VoronoiResult result,
            List<Polygon>? holes,
            float tension = 0.5f,
            int samplesPerBezier = 8)
        {
            if (result is null) throw new ArgumentNullException(nameof(result));
            samplesPerBezier = Math.Max(2, samplesPerBezier);

            var dxfFile = new DxfFile();
            dxfFile.Header.Version = DxfAcadVersion.R2013; // Ensure SPLINE + LWPOLYLINE are supported

            dxfFile.Layers.Add(new DxfLayer("CELLS") { Color = DxfColor.FromIndex(7) }); // White
            dxfFile.Layers.Add(new DxfLayer("BOUNDARY") { Color = DxfColor.FromIndex(1) }); // Red
            dxfFile.Layers.Add(new DxfLayer("HOLES") { Color = DxfColor.FromIndex(5) }); // Blue

            if (result.OriginalBoundary?.Points.Count >= 3)
            {
                var boundaryVertices = new List<DxfLwPolylineVertex>();
                foreach (var point in result.OriginalBoundary.Points)
                {
                    boundaryVertices.Add(new DxfLwPolylineVertex { X = point.X, Y = point.Y });
                }

                var boundaryPolyline = new DxfLwPolyline(boundaryVertices)
                {
                    IsClosed = true,
                    Layer = "BOUNDARY"
                };

                dxfFile.Entities.Add(boundaryPolyline);
            }

            if (holes != null)
            {
                foreach (var hole in holes)
                {
                    if (hole.Points.Count < 3) continue;

                    var holeVertices = new List<DxfLwPolylineVertex>();
                    foreach (var point in hole.Points)
                    {
                        holeVertices.Add(new DxfLwPolylineVertex { X = point.X, Y = point.Y });
                    }

                    var holePolyline = new DxfLwPolyline(holeVertices)
                    {
                        IsClosed = true,
                        Layer = "HOLES"
                    };

                    dxfFile.Entities.Add(holePolyline);
                }
            }

            foreach (var cell in result.Cells)
            {
                if (cell.Points.Count < 3) continue;
                var fit = SampleClosedBezier(cell.Points, tension, samplesPerBezier);
                if (fit.Count < 4) continue;

                var spline = new DxfSpline
                {
                    DegreeOfCurve = 3,
                    IsClosed = true,
                    Layer = "CELLS"
                };

                foreach (var pt in fit)
                    spline.FitPoints.Add(new DxfPoint(pt.X, pt.Y, 0.0));

                dxfFile.Entities.Add(spline);
            }

            using var ms = new MemoryStream();
            dxfFile.Save(ms);
            return ms.ToArray();
        }

        // Sample each cubic Bézier segment uniformly (including segment end).
        private static List<Vector2> SampleClosedBezier(IReadOnlyList<Vector2> poly, float tension, int samplesPerBezier)
        {
            var output = new List<Vector2>();
            // Use centripetal CR with clamped handles to avoid loops
            var segments = SplineUtils.ToClosedCubicBeziersCentripetal(
                poly,
                tension: tension,         // try 0.25
                alpha: 0.5f,              // centripetal
                clampFactor: 0.5f,        // max 50% of smaller adjacent edge
                minCornerAngleDeg: 30f);  // shrink handles for very sharp corners

            if (segments.Count == 0) return output;

            // First anchor
            output.Add(segments[0].P0);

            foreach (var seg in segments)
            {
                for (int i = 1; i <= samplesPerBezier; i++)
                {
                    float t = i / (float)samplesPerBezier;
                    output.Add(SplineUtils.Eval(seg, t));
                }
            }

            return output;
        }

    }
}