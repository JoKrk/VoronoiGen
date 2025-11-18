using System.Collections.Generic;
using System.Numerics;
using System;

namespace VoronoiGen.Services
{
    // Catmull–Rom to cubic Bézier conversion for closed loops.
    // Tension in [0..1]; higher = looser (longer handles).
    public static class SplineUtils
    {
        public readonly struct CubicBezier2D
        {
            public readonly Vector2 P0; // segment start (on-curve)
            public readonly Vector2 C1; // control 1
            public readonly Vector2 C2; // control 2
            public readonly Vector2 P3; // segment end (on-curve)

            public CubicBezier2D(in Vector2 p0, in Vector2 c1, in Vector2 c2, in Vector2 p3)
            {
                P0 = p0; C1 = c1; C2 = c2; P3 = p3;
            }
        }

        // Existing uniform version (kept for compatibility)
        public static List<CubicBezier2D> ToClosedCubicBeziers(IReadOnlyList<Vector2> pts, float tension = 0.5f)
        {
            var curves = new List<CubicBezier2D>();
            if (pts is null || pts.Count < 3) return curves;

            int n = pts.Count;
            for (int i = 0; i < n; i++)
            {
                var p0 = pts[(i - 1 + n) % n];
                var p1 = pts[i];
                var p2 = pts[(i + 1) % n];
                var p3 = pts[(i + 2) % n];

                var b0 = p1;
                var b1 = p1 + (p2 - p0) * (tension / 6f);
                var b2 = p2 - (p3 - p1) * (tension / 6f);
                var b3 = p2;

                curves.Add(new CubicBezier2D(b0, b1, b2, b3));
            }

            return curves;
        }

        // Centripetal (alpha=0.5) Catmull–Rom to cubic Bézier with handle clamping.
        // - alpha: 0=uniform, 0.5=centripetal, 1=chord-length
        // - clampFactor: max handle length as a fraction of the smaller adjacent edge
        // - minCornerAngleDeg: aggressively shortens handles on very sharp corners
        public static List<CubicBezier2D> ToClosedCubicBeziersCentripetal(
            IReadOnlyList<Vector2> pts,
            float tension = 0.5f,
            float alpha = 0.5f,
            float clampFactor = 0.5f,
            float minCornerAngleDeg = 30f)
        {
            var curves = new List<CubicBezier2D>();
            if (pts is null || pts.Count < 3) return curves;

            int n = pts.Count;
            const float eps = 1e-6f;
            float minCornerAngleRad = MathF.Max(0.01f, minCornerAngleDeg) * (MathF.PI / 180f);

            for (int i = 0; i < n; i++)
            {
                var p0 = pts[(i - 1 + n) % n];
                var p1 = pts[i];
                var p2 = pts[(i + 1) % n];
                var p3 = pts[(i + 2) % n];

                float d01 = MathF.Pow(MathF.Max(Vector2.Distance(p0, p1), eps), alpha);
                float d12 = MathF.Pow(MathF.Max(Vector2.Distance(p1, p2), eps), alpha);
                float d23 = MathF.Pow(MathF.Max(Vector2.Distance(p2, p3), eps), alpha);

                float t0 = 0f;
                float t1 = t0 + d01;
                float t2 = t1 + d12;
                float t3 = t2 + d23;

                // Derivatives at p1 and p2
                var m1 = (p2 - p0) / MathF.Max(t2 - t0, eps);
                var m2 = (p3 - p1) / MathF.Max(t3 - t1, eps);
                float dt = t2 - t1;

                // Raw handles
                var b0 = p1;
                var b3 = p2;
                var b1 = p1 + m1 * (dt / 3f) * tension;
                var b2 = p2 - m2 * (dt / 3f) * tension;

                // Angle-aware weighting for sharp corners
                var vIn = p1 - p0;
                var vOut = p2 - p1;
                float lenIn = vIn.Length();
                float lenOut = vOut.Length();
                float angleWeight = 1f;
                if (lenIn > eps && lenOut > eps)
                {
                    vIn /= lenIn; vOut /= lenOut;
                    float cosTheta = MathF.Max(-1f, MathF.Min(1f, Vector2.Dot(vIn, vOut)));
                    float theta = MathF.Acos(cosTheta); // 0..pi
                    if (theta < minCornerAngleRad)
                        angleWeight = theta / minCornerAngleRad; // shrink handles near very sharp corners
                }

                // Clamp handle lengths relative to adjacent edges
                float maxHandle = clampFactor * MathF.Min(lenIn, lenOut) * angleWeight;

                var h1 = b1 - p1;
                var h2 = p2 - b2;
                float lh1 = h1.Length();
                float lh2 = h2.Length();

                if (lh1 > maxHandle && lh1 > eps) b1 = p1 + h1 * (maxHandle / lh1);
                if (lh2 > maxHandle && lh2 > eps) b2 = p2 - h2 * (maxHandle / lh2);

                curves.Add(new CubicBezier2D(b0, b1, b2, b3));
            }

            return curves;
        }

        // Evaluate a cubic Bézier at t ∈ [0,1].
        public static Vector2 Eval(in CubicBezier2D c, float t)
        {
            float u = 1f - t;
            float uu = u * u, tt = t * t;
            return (uu * u) * c.P0 +
                   (3f * uu * t) * c.C1 +
                   (3f * u * tt) * c.C2 +
                   (tt * t) * c.P3;
        }
    }
}