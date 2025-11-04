# voronoi dxf web tool — development guide

## goal
client-side web tool: user uploads a dxf outline, tool generates a voronoi pattern inside the outer boundary (ignore holes by default), user edits parameters, live preview, export svg/dxf. site also needs seo pages + ads.

## architecture
- **frontend/runtime**: blazor webassembly (.net 9)
- **hosting**: cloudflare pages (static files) + cloudflare workers (edge html injection, seo, headers)
- **rendering**: skiasharp wasm via `SkiaSharp.Views.Blazor` (fallback to plain canvas if needed)
- **geometry libs**:
  - dxf i/o: `netDxf`
  - voronoi: `VoronoiLib` or `MIConvexHull`
  - polygon ops: `Clipper2`
- **cdn**: automatic via cloudflare global edge cache
- **consent + ads**: static banner + client-loaded adsense

## core flow
1. **parse dxf**
   - load with `netDxf`
   - extract closed polylines (approximate arcs/splines using chord tolerance)
   - pick largest-area polygon as outer boundary
   - simplify boundary with Douglas-Peucker (tolerance param)

2. **seed generation**
   - basic: uniform random points inside polygon until `seedCount`
   - better: poisson disk sampling with radius param
   - allow fixed rng seed for reproducibility

3. **voronoi diagram**
   - compute via library
   - clip each cell polygon to boundary using `Clipper2`
   - optional lloyd relaxation `k` iterations (recompute seeds at centroids)

4. **render**
   - draw boundary and clipped cells via skiasharp
   - progressive preview: seeds → raw voronoi → clipped result

5. **export**
   - **svg**: one path per cell, correct viewBox
   - **dxf**: write each cell as closed `LWPOLYLINE` via `netDxf`

6. **ui parameters**
   - seed count or density
   - poisson radius
   - lloyd iterations
   - chord tolerance for curves
   - simplify tolerance
   - ignore holes toggle
   - stroke width, join style
   - rng seed

7. **performance**
   - enable wasm aot for release builds
   - use `System.Numerics` vectors
   - simplify boundary early to reduce clip cost
   - cap live preview at ~2000 cells

8. **seo + ads**
   - separate server-rendered landing pages
   - add `sitemap.xml`, metadata, opengraph
   - consent banner for ads (tcf v2.2)
   - place ads outside the canvas area

## project structure

/src
/Host -> asp.net core app (seo pages, static)
/Client -> blazor wasm
/Components
/Services -> dxf loader, voronoi engine, svg exporter
/wwwroot -> index.html, loader scripts
/Shared -> shared models

## nuget packages
- `SkiaSharp.Views.Blazor`
- `netDxf`
- `Clipper2`
- `VoronoiLib` or `MIConvexHull`

## model sketch
```csharp
record Polygon(List<SKPoint> Points);

record DxfImport(
    Polygon Outer,
    List<Polygon> Holes,
    double UnitScale);

record VoronoiParams(
    int SeedCount,
    double PoissonRadius,
    int LloydIterations,
    double ChordTolerance,
    double SimplifyTolerance,
    bool IgnoreHoles,
    int RngSeed);
```

## roadmap

mvp: load dxf, random seeds, single iteration, svg export

v1: poisson + lloyd, dxf export, presets, shareable url params

v2: hole support, offset borders, batch export, theming

## notes

avoid server state

ensure COOP/COEP headers for wasm threads and skiasharp canvas

avoid webgpu rn, webgl via skiasharp is enough

test performance with >2k cells early