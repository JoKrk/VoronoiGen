using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
using VoronoiGen.Services;
using Xunit;

namespace VoronoiGen.Tests;

public class DxfLoaderTests
{
    [Fact]
    public void Load_DoesNotImportArcsAsCircles()
    {
        var bytes = CreateRoundedPlateDxf();

        var import = DxfLoader.Load(
            bytes,
            chordTolerance: 0.05,
            simplifyTolerance: 0,
            closeLineContours: false);

        Assert.InRange(import.Outer.Area(), 12.0, 13.0);
        Assert.Empty(import.Holes);
    }

    [Fact]
    public void Load_StitchesMixedLineAndArcContours()
    {
        var bytes = CreateRoundedPlateDxf();

        var import = DxfLoader.Load(
            bytes,
            chordTolerance: 0.05,
            simplifyTolerance: 0,
            closeLineContours: true);

        var bounds = import.Outer.GetBounds();
        Assert.InRange(import.Outer.Area(), 3977.0, 3980.0);
        Assert.Equal(-50.0, bounds.Left, 3);
        Assert.Equal(-20.0, bounds.Top, 3);
        Assert.Equal(100.0, bounds.Width, 3);
        Assert.Equal(40.0, bounds.Height, 3);
        Assert.Single(import.Holes);
        Assert.InRange(import.Holes[0].Area(), 12.0, 13.0);
    }

    private static byte[] CreateRoundedPlateDxf()
    {
        var dxf = new DxfFile();

        AddLine(dxf, -45, -20, 45, -20);
        AddArc(dxf, 45, -15, 5, 270, 0);
        AddLine(dxf, 50, -15, 50, 15);
        AddArc(dxf, 45, 15, 5, 0, 90);
        AddLine(dxf, 45, 20, -45, 20);
        AddArc(dxf, -45, 15, 5, 90, 180);
        AddLine(dxf, -50, 15, -50, -15);
        AddArc(dxf, -45, -15, 5, 180, 270);

        dxf.Entities.Add(new DxfCircle(new DxfPoint(0, 0, 0), 2));

        using var stream = new MemoryStream();
        dxf.Save(stream);
        return stream.ToArray();
    }

    private static void AddLine(DxfFile dxf, double x1, double y1, double x2, double y2)
    {
        dxf.Entities.Add(new DxfLine(
            new DxfPoint(x1, y1, 0),
            new DxfPoint(x2, y2, 0)));
    }

    private static void AddArc(
        DxfFile dxf,
        double centerX,
        double centerY,
        double radius,
        double startAngle,
        double endAngle)
    {
        dxf.Entities.Add(new DxfArc(
            new DxfPoint(centerX, centerY, 0),
            radius,
            startAngle,
            endAngle));
    }
}
