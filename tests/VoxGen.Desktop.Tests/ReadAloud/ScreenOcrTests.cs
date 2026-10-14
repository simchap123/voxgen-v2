using System.Collections.Generic;
using VoxGen.Desktop.ReadAloud;
using Xunit;

namespace VoxGen.Desktop.Tests.ReadAloud;

public class ScreenOcrTests
{
    private static ScreenOcr.OcrLineBox Line(string text, double y, double x = 100, double w = 400, double h = 16) =>
        new(text, x, y, w, h);

    [Fact]
    public void PickParagraph_ReturnsWholeParagraphUnderCursor_NotNeighbours()
    {
        var lines = new List<ScreenOcr.OcrLineBox>
        {
            Line("Heading far above", 0),
            Line("First line of the paragraph", 100),
            Line("second line continues", 120),
            Line("third line ends.", 140),
            Line("Next paragraph after a gap", 200),
        };

        var text = ScreenOcr.PickParagraph(lines, cx: 200, cy: 125);

        Assert.Equal("First line of the paragraph second line continues third line ends.", text);
    }

    [Fact]
    public void PickParagraph_CursorNotOnText_ReturnsNull()
    {
        var lines = new List<ScreenOcr.OcrLineBox> { Line("Some text", 100) };
        Assert.Null(ScreenOcr.PickParagraph(lines, cx: 200, cy: 300));
    }

    [Fact]
    public void PickParagraph_SeparateColumns_AreNotMerged()
    {
        var lines = new List<ScreenOcr.OcrLineBox>
        {
            Line("Left column", 100, x: 0, w: 300),
            Line("Right column", 118, x: 600, w: 300),
        };
        Assert.Equal("Left column", ScreenOcr.PickParagraph(lines, cx: 100, cy: 105));
    }

    [Fact]
    public void PickParagraph_NoLines_ReturnsNull() =>
        Assert.Null(ScreenOcr.PickParagraph(new List<ScreenOcr.OcrLineBox>(), 0, 0));
}
