using Clipper2Lib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoronoiGen.Models;
using static VoronoiGen.Services.VoronoiEngine.GeodesicVoronoiGraph;

namespace VoronoiGen.Services
{
    // Computes Voronoi diagram via half-plane clipping against polygon-with-holes and optional offsets.
    // Generation now happens strictly inside the offset boundary: seeds outside the offseted domain are ignored
    // and do not influence the result. The returned boundary is still the original to preserve API behavior.
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

            // Determine active seeds: strictly inside the offseted generation domain (outer minus holes)
            var openBoundaryForPointTest = ToOpen(clipOuter.Points);
            var openHolesForPointTest = clipHoles.Select(h => ToOpen(h.Points)).ToList();

            bool[] isActiveSeed = new bool[seeds.Count];
            for (int i = 0; i < seeds.Count; i++)
            {
                var s = seeds[i];
                bool insideOuter = IsInsideOrOn(openBoundaryForPointTest, s);
                bool insideAnyHole = false;
                if (insideOuter && openHolesForPointTest.Count > 0)
                {
                    for (int h = 0; h < openHolesForPointTest.Count; h++)
                    {
                        if (IsInsideOrOn(openHolesForPointTest[h], s))
                        {
                            insideAnyHole = true;
                            break;
                        }
                    }
                }
                isActiveSeed[i] = insideOuter && !insideAnyHole;
            }

            // Precompute hole paths for boolean clipping (Clipper2)
            var holePaths = new PathsD();
            if (clipHoles.Count > 0)
            {
                foreach (var h in clipHoles)
                {
                    var open = ToOpen(h.Points);
                    if (open.Count >= 3)
                        holePaths.Add(ToPathD(open));
                }
            }

            var cells = new List<Polygon>(seeds.Count);

            for (int i = 0; i < seeds.Count; i++)
            {
                // Only generate cells for active seeds; inactive seeds produce empty cells and do not participate.
                if (!isActiveSeed[i])
                {
                    cells.Add(new Polygon(new List<Vector2>()));
                    continue;
                }

                var cell = new List<(double X, double Y)>(openBoundary);
                var A = seeds[i];

                // Voronoi half-plane clipping (only active competitors)
                for (int j = 0; j < seeds.Count; j++)
                {
                    if (i == j) continue;
                    if (!isActiveSeed[j]) continue;

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
                    // Subtract holes using robust boolean clipping
                    if (holePaths.Count > 0)
                    {
                        var cellPath = new PathsD { ToPathD(cell) };
                        var solution = Clipper.Difference(cellPath, holePaths, FillRule.NonZero);

                        if (solution.Count == 0)
                        {
                            cells.Add(new Polygon(new List<Vector2>()));
                            continue;
                        }

                        // pick largest remaining component (Polygon model is single-ring)
                        var best = solution
                            .OrderByDescending(p => Math.Abs(Clipper.Area(p)))
                            .First();

                        cell = best.Select(pt => (pt.x, pt.y)).ToList();
                    }

                    // Final safety: force cells to lie inside the OFFSET boundary (generation domain)
                    if (cell.Count >= 3 && openBoundary.Count >= 3)
                    {
                        var offsetOpen = ToOpen(clipOuter.Points);
                        cell = ClipInsidePolygon(cell, offsetOpen);
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

        // NEW: Geodesic Voronoi core (CDT + multi-source Dijkstra).
        // Produces a graph (segments) of Voronoi boundaries wrapped around holes/concavities.
        // Next step is polygonization per site using these segments.
        public static GeodesicVoronoiGraph ComputeGeodesicGraph(Polygon boundary, List<Polygon>? holes, bool ignoreHoles, List<Vector2> seeds)
        {
            // 1) Build constrained triangulation that respects outer and hole contours.
            var outerOpen = ToOpen(boundary.Points);
            var holeOpens = (!ignoreHoles && holes is { Count: > 0 })
                ? holes.Select(h => ToOpen(h.Points)).Where(h => h.Count >= 3).ToList()
                : new List<List<Vector2>>();

            // Sanity: need at least a triangle to proceed.
            if (outerOpen.Count < 3)
                return GeodesicVoronoiGraph.Empty();

            // Triangle.NET aliasing to avoid name clash with our Polygon.
            TriangleNet.Geometry.Polygon triPolygon = new();

            // Outer contour (CCW preferred, but constraints handle it)
            var outerVerts = new List<TriangleNet.Geometry.Vertex>(outerOpen.Count);
            foreach (var v in outerOpen) outerVerts.Add(new TriangleNet.Geometry.Vertex(v.X, v.Y));
            triPolygon.Add(new TriangleNet.Geometry.Contour(outerVerts), hole: false);

            // Holes (as hole contours)
            foreach (var hole in holeOpens)
            {
                var hv = new List<TriangleNet.Geometry.Vertex>(hole.Count);
                foreach (var p in hole) hv.Add(new TriangleNet.Geometry.Vertex(p.X, p.Y));
                triPolygon.Add(new TriangleNet.Geometry.Contour(hv), hole: true);
            }

            var cOptions = new TriangleNet.Meshing.ConstraintOptions
            {
                ConformingDelaunay = true
            };
            var qOptions = new TriangleNet.Meshing.QualityOptions
            {
                MinimumAngle = 20
            };

            var mesh = triPolygon.Triangulate(cOptions, qOptions); // IMesh

            // Extract mesh vertices and triangles.
            var meshVerts = mesh.Vertices.Select(v => new Vector2((float)v.X, (float)v.Y)).ToList();
            var idToIndex = new Dictionary<int, int>(meshVerts.Count);
            int idx = 0;
            foreach (var v in mesh.Vertices) idToIndex[v.ID] = idx++;

            var triangles = new List<(int A, int B, int C)>();
            foreach (var t in mesh.Triangles)
            {
                var v0 = t.GetVertex(0);
                var v1 = t.GetVertex(1);
                var v2 = t.GetVertex(2);
                triangles.Add((idToIndex[v0.ID], idToIndex[v1.ID], idToIndex[v2.ID]));
            }

            // 2) Build undirected adjacency from triangle edges (dedup with set).
            var edgeSet = new HashSet<(int, int)>();
            var adj = new List<List<(int to, double w)>>(meshVerts.Count);
            for (int i = 0; i < meshVerts.Count; i++) adj.Add(new List<(int to, double w)>());

            void AddEdge(int a, int b)
            {
                if (a == b) return;
                var key = a < b ? (a, b) : (b, a);
                if (edgeSet.Add(key))
                {
                    double w = Vector2.Distance(meshVerts[a], meshVerts[b]);
                    adj[a].Add((b, w));
                    adj[b].Add((a, w));
                }
            }

            foreach (var (A, B, C) in triangles)
            {
                AddEdge(A, B);
                AddEdge(B, C);
                AddEdge(C, A);
            }

            // 3) Multi-source Dijkstra: label each vertex by nearest seed via walkable paths.
            var n = meshVerts.Count;
            var dist = new double[n];
            var owner = new int[n];
            var visited = new bool[n];
            Array.Fill(dist, double.PositiveInfinity);
            Array.Fill(owner, -1);

            var pq = new PriorityQueue<int, double>();

            // Seed initialization: use containing triangle if possible,
            // otherwise snap to nearest vertex.
            static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
            {
                // Barycentric (same-side test)
                float s1 = Cross(b - a, p - a);
                float s2 = Cross(c - b, p - b);
                float s3 = Cross(a - c, p - c);
                bool hasNeg = (s1 < 0) || (s2 < 0) || (s3 < 0);
                bool hasPos = (s1 > 0) || (s2 > 0) || (s3 > 0);
                const float eps = 1e-7f;
                return !(hasNeg && hasPos) || (Math.Abs(s1) < eps || Math.Abs(s2) < eps || Math.Abs(s3) < eps);
            }

            // Determine active seeds w.r.t. outer minus holes
            bool IsInsideDomain(Vector2 s)
            {
                var insideOuter = IsInsideOrOn(outerOpen, s);
                if (!insideOuter) return false;
                foreach (var h in holeOpens)
                {
                    if (IsInsideOrOn(h, s)) return false;
                }
                return true;
            }

            for (int si = 0; si < seeds.Count; si++)
            {
                var s = seeds[si];
                if (!IsInsideDomain(s)) continue;

                // find containing triangle
                (int A, int B, int C)? found = null;
                for (int t = 0; t < triangles.Count; t++)
                {
                    var (a, b, c) = triangles[t];
                    if (PointInTri(s, meshVerts[a], meshVerts[b], meshVerts[c]))
                    {
                        found = (a, b, c);
                        break;
                    }
                }

                if (found is { } tri)
                {
                    void SeedVertex(int v)
                    {
                        double d0 = Vector2.Distance(s, meshVerts[v]);
                        if (d0 + 1e-9 < dist[v])
                        {
                            dist[v] = d0;
                            owner[v] = si;
                            pq.Enqueue(v, d0);
                        }
                    }
                    SeedVertex(tri.A);
                    SeedVertex(tri.B);
                    SeedVertex(tri.C);
                }
                else
                {
                    // snap to nearest vertex
                    int best = -1; double bestD = double.PositiveInfinity;
                    for (int v = 0; v < meshVerts.Count; v++)
                    {
                        double d0 = Vector2.Distance(s, meshVerts[v]);
                        if (d0 < bestD) { bestD = d0; best = v; }
                    }
                    if (best >= 0)
                    {
                        if (bestD + 1e-9 < dist[best])
                        {
                            dist[best] = bestD;
                            owner[best] = si;
                            pq.Enqueue(best, bestD);
                        }
                    }
                }
            }

            // Dijkstra
            while (pq.Count > 0)
            {
                if (!pq.TryDequeue(out int u, out double du)) break;
                if (du > dist[u] + 1e-12) continue;
                if (visited[u]) continue;
                visited[u] = true;

                foreach (var (v, w) in adj[u])
                {
                    double nd = du + w;
                    if (nd + 1e-12 < dist[v])
                    {
                        dist[v] = nd;
                        owner[v] = owner[u];
                        pq.Enqueue(v, nd);
                    }
                    // If equal distances and different owners appear, we keep the earlier one;
                    // true tie handling could store multiple owners, but not required for a start.
                }
            }

            // 4) Edge-crossing extraction: place equal-distance point on edges with differing owners.
            var edgeCuts = new Dictionary<(int a, int b), (Vector2 P, int OA, int OB)>(edgeSet.Count);
            foreach (var key in edgeSet)
            {
                int a = key.Item1, b = key.Item2;
                int oa = owner[a], ob = owner[b];
                if (oa < 0 || ob < 0 || oa == ob) continue;

                var pa = meshVerts[a];
                var pb = meshVerts[b];
                double L = Vector2.Distance(pa, pb);
                if (L < 1e-12) continue;

                // Solve: distA(pa) + t*L == distB(pb) + (1 - t)*L
                double t = (dist[b] + L - dist[a]) / (2.0 * L);
                t = Math.Clamp(t, 0.0, 1.0);
                var P = pa + (float)t * (pb - pa);

                edgeCuts[key] = (P, oa, ob);
            }

            // 5) Inside each triangle, connect crossing points to form boundary segments.
            var segments = new List<GeodesicVoronoiGraph.Segment>(edgeCuts.Count);
            foreach (var (A, B, C) in triangles)
            {
                var eAB = MakeKey(A, B);
                var eBC = MakeKey(B, C);
                var eCA = MakeKey(C, A);

                var cuts = new List<((int, int) key, Vector2 P, int OA, int OB)>(3);
                if (edgeCuts.TryGetValue(eAB, out var cAB)) cuts.Add((eAB, cAB.P, cAB.OA, cAB.OB));
                if (edgeCuts.TryGetValue(eBC, out var cBC)) cuts.Add((eBC, cBC.P, cBC.OA, cBC.OB));
                if (edgeCuts.TryGetValue(eCA, out var cCA)) cuts.Add((eCA, cCA.P, cCA.OA, cCA.OB));

                if (cuts.Count == 2)
                {
                    var p0 = cuts[0].P;
                    var p1 = cuts[1].P;
                    // Owners on both sides: pick the pair union (usually same pair for both cuts)
                    var oa = cuts[0].OA;
                    var ob = cuts[0].OB;
                    segments.Add(new GeodesicVoronoiGraph.Segment(p0, p1, oa, ob));
                }
                else if (cuts.Count == 3)
                {
                    // Rare 3-label triangles: connect the three cuts cyclically.
                    // This creates a small 3-arm junction (good enough to start).
                    segments.Add(new GeodesicVoronoiGraph.Segment(cuts[0].P, cuts[1].P, cuts[0].OA, cuts[0].OB));
                    segments.Add(new GeodesicVoronoiGraph.Segment(cuts[1].P, cuts[2].P, cuts[1].OA, cuts[1].OB));
                    segments.Add(new GeodesicVoronoiGraph.Segment(cuts[2].P, cuts[0].P, cuts[2].OA, cuts[2].OB));
                }
            }

            // Build final graph
            var edgesList = edgeSet.Select(k => (k.Item1, k.Item2)).ToList();
            return new GeodesicVoronoiGraph(meshVerts, edgesList, owner, dist, segments);

            static (int, int) MakeKey(int a, int b) => a < b ? (a, b) : (b, a);
            static float Cross(in Vector2 a, in Vector2 b) => a.X * b.Y - a.Y * b.X;
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
        // NOTE: This approach (outside half-planes intersection) is not robust for holes; kept for reference, no longer used.
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

        // Point-in-polygon that also treats points on edges as inside
        private static bool IsInsideOrOn(IReadOnlyList<Vector2> poly, Vector2 p, float tol = 1e-5f)
        {
            if (poly.Count < 3) return false;
            if (PointOnPolyline(poly, p, tol)) return true;
            return PointInPolygon(poly, p);
        }

        private static bool PointInPolygon(IReadOnlyList<Vector2> poly, Vector2 p)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var pi = poly[i];
                var pj = poly[j];

                bool intersect = ((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                                 (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / ((pj.Y - pi.Y) == 0 ? float.Epsilon : (pj.Y - pi.Y)) + pi.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }

        private static bool PointOnPolyline(IReadOnlyList<Vector2> poly, Vector2 p, float tol)
        {
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                if (DistancePointToSegment(a, b, p) <= tol) return true;
            }
            return false;
        }

        private static float DistancePointToSegment(in Vector2 a, in Vector2 b, in Vector2 p)
        {
            var ab = b - a;
            var ap = p - a;
            float ab2 = Vector2.Dot(ab, ab);
            if (ab2 <= float.Epsilon) return (p - a).Length();
            float t = Math.Clamp(Vector2.Dot(ap, ab) / ab2, 0f, 1f);
            var c = a + t * ab;
            return (p - c).Length();
        }

        // Helpers for Clipper2 conversions
        private static PathD ToPathD(IEnumerable<(double X, double Y)> poly)
        {
            var path = new PathD();
            foreach (var (X, Y) in poly) path.Add(new PointD(X, Y));
            return path;
        }

        private static PathD ToPathD(IEnumerable<Vector2> poly)
        {
            var path = new PathD();
            foreach (var p in poly) path.Add(new PointD(p.X, p.Y));
            return path;
        }

        // Lightweight graph/result for geodesic Voronoi; polygonization can be layered on top.
        public record GeodesicVoronoiGraph(
            List<Vector2> MeshVertices,
            List<(int A, int B)> MeshEdges,
            int[] OwnerByVertex,
            double[] DistanceByVertex,
            List<Segment> Segments)
        {
            public record struct Segment(Vector2 A, Vector2 B, int OwnerA, int OwnerB);

            public static GeodesicVoronoiGraph Empty()
            {
                return new GeodesicVoronoiGraph(
                    new List<Vector2>(),
                    new List<(int A, int B)>(),
                    Array.Empty<int>(),
                    Array.Empty<double>(),
                    new List<Segment>());
            }
        }
    }
}