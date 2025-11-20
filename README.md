# VoronoiGen

VoronoiGen is a Blazor WebAssembly (.NET 9) tool that generates Voronoi-based lightweight infill / generative patterns from uploaded DXF boundary geometry, with optional holes, smoothing, filtering, and DXF export (polylines or spline-fitted curves).

> All processing runs client-side in the browser. No file contents are uploaded to a server.

## Features

- Upload a closed-outline DXF (outer boundary + optional holes)
- Automatic polygon simplification (tolerance configurable in code)
- Poisson disk seed generation with deterministic RNG fallback
- Optional Lloyd relaxation (centroid adjustment)
- Cell filtering: minimum area and maximum aspect ratio
- Cell gap + inner / outer offsets to control free space
- Corner rounding (explicit radius or auto) + Chaikin smoothing
- Export DXF as polylines or Catmull–Rom spline samples
- Client-side download (no server dependency)
- Cloudflare Pages deployment via GitHub Actions

## How It Works (Pipeline)

1. Load DXF: `DxfLoader.Load(...)` produces `Outer` polygon + `Holes`.
2. Generate seeds:
   - Try Poisson (`SeedGenerator.GeneratePoisson`)
   - Fallback random uniform (`SeedGenerator.GenerateRandom`) if Poisson yields zero
3. (Optional) Lloyd relaxation passes reposition seeds (`VoronoiEngine.ComputeWithLloyd`)
4. Build raw Voronoi cells and apply:
   - Offsets (outer inset, hole expansion)
   - Gap shrink (uniform inward expansion of each cell)
   - Smoothing iterations
   - Aspect/area filtering
5. Rounding:
   - Explicit fillet radius OR auto (radius derived from gap + smoothing)
   - Chaikin light pass with configurable weight
6. Export:
   - Polylines: direct processed vertices
   - Splines: sampled Catmull–Rom segments (`DxfExporter.ExportSplines`)
7. Download DXF via JS interop (`voronoiGen.downloadBlob`)

## Parameter Reference

| Group                | Name                | Purpose |
|---------------------|---------------------|---------|
| Boundary            | Use Holes           | Include hole polygons as forbidden areas |
| Boundary            | Outer Offset        | Inset outer boundary (shrinks usable area) |
| Boundary            | Inner Offset        | Expand holes outward (reduces usable area) |
| Seeds               | Min Spacing (Poisson)| Poisson disk radius (higher = fewer seeds) |
| Seeds               | Seed Count          | Fallback random seed count |
| Seeds               | RNG Seed            | Deterministic random source |
| Seeds               | Lloyd Iterations    | Centroid relaxation passes |
| Cells               | Cell Gap            | Uniform shrink of each cell |
| Cells               | Smooth Iterations   | Polygon smoothing before rounding |
| Cells               | Min Cell Area       | Filters out too-small cells |
| Cells               | Max Aspect Ratio    | Filters elongated cells (0 disables) |
| Rounding            | Corner Radius       | Explicit fillet radius (0 = auto) |
| Rounding            | Chaikin Weight      | Secondary rounding intensity |
| Rounding            | Arc Tolerance       | Tessellation tolerance for arcs (0 = auto) |
| Export              | Mode                | Polylines vs Splines output |
| Export (Splines)    | Spline Tension      | Catmull–Rom tension (0–1) |
| Export (Splines)    | Samples / Segment   | Fit points per cubic segment |
