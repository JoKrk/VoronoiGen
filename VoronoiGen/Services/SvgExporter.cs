using System.Globalization;
using System.Linq;
using System.Text;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    // Exports current result to a compact inline SVG string with separate layers for cells, holes, and boundary.
    // Integration: Home page calls Export to let the user save the generated pattern.
    public static class SvgExporter
    {
        public static string Export(VoronoiResult result, List<Polygon>? holes = null, float strokeWidth = 1f)
        {
            var bounds = result.OriginalBoundary.GetBounds();
            var sb = new StringBuilder();

            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" ");
            sb.AppendFormat(CultureInfo.InvariantCulture, "viewBox=\"{0} {1} {2} {3}\">",
                bounds.Left, bounds.Top, bounds.Width, bounds.Height);

            // Layer 1: Outer Boundary (red stroke)
            sb.Append("<g id=\"boundary\" fill=\"none\" stroke=\"#FF0000\" ");
            sb.AppendFormat(CultureInfo.InvariantCulture, "stroke-width=\"{0}\">", strokeWidth * 1.5f);
            if (result.OriginalBoundary.Points.Count >= 3)
            {
                sb.Append("<path d=\"");
                for (int i = 0; i < result.OriginalBoundary.Points.Count; i++)
                {
                    var p = result.OriginalBoundary.Points[i];
                    sb.Append(i == 0 ? "M" : "L");
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1}", p.X, p.Y);
                }
                sb.Append("Z\"/>");
            }
            sb.Append("</g>");

            // Layer 2: Internal Holes (blue stroke)
            if (holes != null && holes.Count > 0)
            {
                sb.Append("<g id=\"holes\" fill=\"none\" stroke=\"#0000FF\" ");
                sb.AppendFormat(CultureInfo.InvariantCulture, "stroke-width=\"{0}\">", strokeWidth * 1.5f);
                foreach (var hole in holes)
                {
                    if (hole.Points.Count < 3) continue;
                    sb.Append("<path d=\"");
                    for (int i = 0; i < hole.Points.Count; i++)
                    {
                        var p = hole.Points[i];
                        sb.Append(i == 0 ? "M" : "L");
                        sb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1}", p.X, p.Y);
                    }
                    sb.Append("Z\"/>");
                }
                sb.Append("</g>");
            }

            // Layer 3: Voronoi Cells (black stroke)
            sb.Append("<g id=\"cells\" fill=\"none\" stroke=\"#000000\" ");
            sb.AppendFormat(CultureInfo.InvariantCulture, "stroke-width=\"{0}\">", strokeWidth);
            foreach (var cell in result.Cells)
            {
                if (cell.Points.Count < 3) continue;
                sb.Append("<path d=\"");
                for (int i = 0; i < cell.Points.Count; i++)
                {
                    var p = cell.Points[i];
                    sb.Append(i == 0 ? "M" : "L");
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1}", p.X, p.Y);
                }
                sb.Append("Z\"/>");
            }
            sb.Append("</g>");

            sb.Append("</svg>");
            return sb.ToString();
        }
    }
}