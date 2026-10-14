using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace VoxGen.Desktop.ReadAloud;

/// <summary>
/// Fallback for apps that don't expose their text to UI Automation (images, scanned PDFs,
/// custom-drawn UIs): screenshot the area around the cursor and run Windows' built-in OCR
/// (Windows.Media.Ocr — local, free, no package). Returns the paragraph under the cursor.
/// </summary>
public static class ScreenOcr
{
    // Capture box around the cursor: wide enough for a line of text, tall enough for a paragraph.
    private const int HalfWidth = 700;
    private const int Above = 160;
    private const int Below = 320;

    private static readonly Lazy<OcrEngine?> Engine = new(OcrEngine.TryCreateFromUserProfileLanguages);

    public static bool Available => Engine.Value is not null;

    /// <summary>What the last OCR pass saw — for the diagnostic log line.</summary>
    public static string LastDiagnostics { get; private set; } = "";

    public static async Task<string?> ReadAroundAsync(int x, int y)
    {
        var engine = Engine.Value;
        if (engine is null) { LastDiagnostics = "no OCR engine"; return null; }

        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var left = Math.Max(screen.Left, x - HalfWidth);
        var top = Math.Max(screen.Top, y - Above);
        var right = Math.Min(screen.Right, x + HalfWidth);
        var bottom = Math.Min(screen.Bottom, y + Below);
        if (right - left < 20 || bottom - top < 20) return null;

        using var bmp = new Bitmap(right - left, bottom - top, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(left, top, 0, 0, bmp.Size);
        }

        var result = await engine.RecognizeAsync(await ToSoftwareBitmapAsync(bmp));
        var lines = result.Lines
            .Select(l => new OcrLineBox(l.Text, Union(l.Words.Select(w => w.BoundingRect))))
            .Where(l => l.Width > 0)
            .ToList();

        var picked = PickParagraph(lines, x - left, y - top);
        LastDiagnostics = $"capture={left},{top},{right - left}x{bottom - top} cursorInImage={x - left},{y - top} lines={lines.Count} " +
                          string.Join(" | ", lines.Take(6).Select(l => $"[{l.X:0},{l.Y:0},{l.Width:0}x{l.Height:0}] {Trunc(l.Text)}")) +
                          $" picked={(picked is null ? "none" : picked.Length + " chars")}";
        return picked;
    }

    /// <summary>A recognized line and its bounds within the captured image.</summary>
    public readonly record struct OcrLineBox(string Text, double X, double Y, double Width, double Height)
    {
        public OcrLineBox(string text, Windows.Foundation.Rect r) : this(text, r.X, r.Y, r.Width, r.Height) { }
        public double Bottom => Y + Height;
        public double Right => X + Width;
    }

    /// <summary>
    /// The line under (or nearest above/below within half a line of) the cursor, extended up and
    /// down through neighbouring lines that look like the same paragraph (small vertical gap,
    /// overlapping horizontally). Pure — unit-tested.
    /// </summary>
    public static string? PickParagraph(IReadOnlyList<OcrLineBox> lines, double cx, double cy)
    {
        if (lines.Count == 0) return null;
        var sorted = lines.OrderBy(l => l.Y).ToList();

        var hit = sorted.FindIndex(l => cy >= l.Y - l.Height * 0.5 && cy <= l.Bottom + l.Height * 0.5
                                         && cx >= l.X - 40 && cx <= l.Right + 40);
        if (hit < 0) return null;

        bool SameParagraph(OcrLineBox a, OcrLineBox b)
        {
            var gap = b.Y - a.Bottom;
            var lineH = Math.Max(a.Height, b.Height);
            var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
            return gap < lineH * 0.9 && overlap > 0;
        }

        var start = hit;
        while (start > 0 && SameParagraph(sorted[start - 1], sorted[start])) start--;
        var end = hit;
        while (end < sorted.Count - 1 && SameParagraph(sorted[end], sorted[end + 1])) end++;

        return string.Join(" ", sorted.Skip(start).Take(end - start + 1).Select(l => l.Text));
    }

    private static string Trunc(string t) => t.Length <= 30 ? t : t[..30] + "…";

    private static Windows.Foundation.Rect Union(IEnumerable<Windows.Foundation.Rect> rects)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var x in rects)
        {
            l = Math.Min(l, x.X); t = Math.Min(t, x.Y);
            r = Math.Max(r, x.X + x.Width); b = Math.Max(b, x.Y + x.Height);
        }
        return l == double.MaxValue ? default : new Windows.Foundation.Rect(l, t, r - l, b - t);
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;
        using var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras))
        {
            writer.WriteBytes(ms.ToArray());
            await writer.StoreAsync();
            writer.DetachStream();
        }
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
