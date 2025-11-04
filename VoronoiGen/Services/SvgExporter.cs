using System.Globalization;
using System.Linq;
using System.Text;
using VoronoiGen.Models;

namespace VoronoiGen.Services
{
    // Exports current result to a compact inline SVG string. Can be offered as a download via <a download>.
    // Integration: Home page calls Export to let the user save the generated pattern.
    public static class SvgExporter
    {
        public static string Export(VoronoiResult result, float strokeWidth = 1f)
        {
            var bounds = result.Boundary.GetBounds();
            var sb = new StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" ");
            sb.AppendFormat(CultureInfo.InvariantCulture, "viewBox=\"{0} {1} {2} {3}\">", bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            sb.Append("<g fill=\"none\" stroke=\"black\" ");
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

            sb.Append("</g></svg>");
            return sb.ToString();
        }
    }
}
