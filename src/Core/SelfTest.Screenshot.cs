namespace OmlCli;

static partial class SelfTest
{
    // oml browser --max-width/--max-height: the size math (aspect ratio kept, never enlarged, both limits), the token
    // estimate, the PNG header read and the box-filter shrink.
    static void ScreenshotSizeTest(List<string> failures)
    {
        void Is((int, int) got, (int, int) want, string what) { if (got != want) failures.Add($"screenshot size: {what} gave {got}, expected {want}"); }
        Is(ScreenshotSize.Fit(1366, 900, 800, null), (800, 527), "1366x900 to width 800");
        Is(ScreenshotSize.Fit(1366, 900, 2000, null), (1366, 900), "a limit above the capture (never enlarged)");
        Is(ScreenshotSize.Fit(1366, 900, null, null), (1366, 900), "no limit");
        Is(ScreenshotSize.Fit(1366, 900, 0, 0), (1366, 900), "0 is no limit");
        Is(ScreenshotSize.Fit(1366, 900, 1000, 300), (455, 300), "the height limit is the tighter one");
        Is(ScreenshotSize.Fit(390, 4000, 800, 1000), (98, 1000), "a tall capture");
        Is(ScreenshotSize.Fit(3, 3000, 1, null), (1, 1000), "a one-pixel limit");
        if (ScreenshotSize.Tokens(1366, 900) != 1639) failures.Add($"screenshot size: tokens for 1366x900 are {ScreenshotSize.Tokens(1366, 900)}, expected 1639");
        var report = ScreenshotSize.Report(800, 527, 1366, 900);
        if (report.ToJsonString() != """{"width":800,"height":527,"tokens":562,"scaledFrom":"1366x900"}""") failures.Add($"screenshot size: report {report.ToJsonString()}");
        if (ScreenshotSize.Report(800, 527, 800, 527)["scaledFrom"] is not null) failures.Add("screenshot size: scaledFrom on a capture that was not shrunk");
        byte[] header = [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 5, 0x56, 0, 0, 3, 0x84];
        Is(ScreenshotSize.PngSize(header), (1366, 900), "PNG header");
        Is(ScreenshotSize.PngSize(new byte[24]), (0, 0), "not a PNG");
        // 4x2 pixels to 2x1: each target pixel averages a 2x2 block.
        byte[] pixels = [0, 0, 0, 255, 100, 100, 100, 255, 10, 20, 30, 255, 10, 20, 30, 255,
                         0, 0, 0, 255, 100, 100, 100, 255, 10, 20, 30, 255, 10, 20, 30, 255];
        var small = ScreenshotSize.Shrink(pixels, 4, 2, 2, 1);
        if (!small.SequenceEqual(new byte[] { 50, 50, 50, 255, 10, 20, 30, 255 })) failures.Add($"screenshot size: shrink gave {string.Join(",", small)}");
        if (!ReferenceEquals(ScreenshotSize.Shrink(pixels, 4, 2, 4, 2), pixels)) failures.Add("screenshot size: shrink to the same size copied the pixels");
        // A PNG round trip: written, shrunk to width 2, read back; a PNG within the limit comes back as the same bytes.
        var png = ScreenshotSize.Encode(pixels, 4, 2);
        Is(ScreenshotSize.PngSize(png), (4, 2), "encoded PNG");
        var shrunk = ScreenshotSize.ShrinkPng(png, 2, null);
        Is(ScreenshotSize.PngSize(shrunk), (2, 1), "PNG shrunk to width 2");
        if (ScreenshotSize.Decode(shrunk) is not { } back || !back.SequenceEqual(small)) failures.Add("screenshot size: the shrunk PNG does not read back as the shrunk pixels");
        if (!ReferenceEquals(ScreenshotSize.ShrinkPng(png, 4, 2), png)) failures.Add("screenshot size: a PNG within the limit was rewritten");
    }
}
