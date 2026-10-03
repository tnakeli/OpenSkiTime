using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Tesseract;

namespace OpenSkiTime.Recognition;

public sealed partial class TesseractTimingImageRecognizer(string? modelDirectory = null) : ITimingImageRecognizer
{
    private const int ContrastWindowRadius = 15;
    private const int ContrastBorder = ContrastWindowRadius + 1;
    [GeneratedRegex(@"^[ \t]*[0-9]{1,6}[ \t]+C[01OI]{1,2}M?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReceiptPrefix();
    [GeneratedRegex(@"^C[01OI]M$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReceiptChannel();
    public Task<TimingImageText> RecognizeAsync(byte[] image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length is 0 or > 20_000_000) { throw new DomainValidationException("Choose a PNG/JPEG image smaller than 20 MB."); }
        return Task.Run(() => Recognize(image, ct), ct);
    }

    private TimingImageText Recognize(byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = modelDirectory ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (!File.Exists(Path.Combine(directory, "eng.traineddata")))
        { throw new DomainValidationException("The offline OCR model is missing. Rebuild or reinstall OpenSkiTime with its tessdata folder."); }
        try
        {
            using var pixels = Pix.LoadFromMemory(bytes);
            if ((long)pixels.Width * pixels.Height > 40_000_000 || pixels.Width < 20 || pixels.Height < 20)
            { throw new DomainValidationException("The image must be between 20 pixels per side and 40 megapixels. Crop the receipt and try again."); }
            using var engine = new TesseractEngine(directory, "eng", EngineMode.LstmOnly);
            engine.SetVariable("preserve_interword_spaces", "1");
            var words = new List<RecognizedTimingLine>();
            var lines = ReadLines(engine, pixels, new(0, 0, pixels.Width, pixels.Height), ct, words);
            var receiptColumn = FindReceiptColumn(lines, pixels.Width, pixels.Height);
            var useLocalContrast = receiptColumn is null;
            receiptColumn ??= FindChannelColumn(words, pixels.Width, pixels.Height);
            if (receiptColumn is { } column)
            {
                var receiptLines = ReadReceiptRows(engine, pixels, column, useLocalContrast, ct);
                if (receiptLines.Count > 0)
                {
                    receiptLines.AddRange(lines.Where(x => x.X + x.Width <= column.X1 || x.X >= column.X2
                        || x.Y + x.Height <= column.Y1 || x.Y >= column.Y2)
                        .Select(line => MarkImageBoundary(line, pixels.Width, pixels.Height)));
                    return new("Tesseract 5.2 / tessdata_fast 8741641 / receipt row alternatives"
                        + (useLocalContrast ? " / localized contrast" : ""), receiptLines.OrderBy(x => x.Y).ToArray());
                }
            }
            return new("Tesseract 5.2 / tessdata_fast 8741641",
                lines.Select(line => MarkImageBoundary(line, pixels.Width, pixels.Height)).ToArray());
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        { throw new DomainValidationException("Offline OCR could not load. Install the Microsoft Visual C++ 2015-2022 runtime (x64) and retain the application's native OCR libraries."); }
    }

    private static List<RecognizedTimingLine> ReadLines(TesseractEngine engine, Pix pixels, Rect area, CancellationToken ct,
        List<RecognizedTimingLine>? words = null)
    {
        using var page = engine.Process(pixels, area, PageSegMode.SingleBlock);
        using var iterator = page.GetIterator();
        var lines = new List<RecognizedTimingLine>();
        iterator.Begin();
        do
        {
            ct.ThrowIfCancellationRequested();
            var text = iterator.GetText(PageIteratorLevel.TextLine)?.Trim();
            if (string.IsNullOrEmpty(text)) { continue; }
            if (iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var rect))
            { lines.Add(new(text, rect.X1, rect.Y1, rect.Width, rect.Height)); }
        } while (iterator.Next(PageIteratorLevel.TextLine));
        if (words is not null)
        {
            iterator.Begin();
            do
            {
                ct.ThrowIfCancellationRequested();
                var word = iterator.GetText(PageIteratorLevel.Word)?.Trim();
                if (!string.IsNullOrEmpty(word)
                    && iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var rect))
                { words.Add(new(word, rect.X1, rect.Y1, rect.Width, rect.Height)); }
            } while (iterator.Next(PageIteratorLevel.Word));
        }
        return lines;
    }

    private static Rect? FindChannelColumn(List<RecognizedTimingLine> words, int width, int height)
    {
        // A textured background can become leading text on every line. Locate the repeated
        // fixed-width ALGE channel words independently of those contaminated line bounds.
        var anchors = words.Where(x => x.Height is >= 6 and <= 100 && x.Width > x.Height
            && ReceiptChannel().IsMatch(x.Text)).ToArray();
        if (anchors.Length < 6) { return null; }
        var cluster = anchors.Select(anchor => anchors.Where(x =>
            Math.Abs(x.X - anchor.X) <= Math.Min(x.Width, anchor.Width)
            && x.Height * 2 >= anchor.Height && x.Height <= anchor.Height * 2).ToArray())
            .MaxBy(x => x.Length)!;
        if (cluster.Length < 6) { return null; }
        var lineHeight = cluster.Select(x => x.Height).Order().ElementAt(cluster.Length / 2);
        if (cluster.Max(x => x.Y) - cluster.Min(x => x.Y) < 8 * lineHeight) { return null; }
        var characterWidth = cluster.Select(x => (x.Width + 2) / 3).Order().ElementAt(cluster.Length / 2);
        var left = Math.Max(0, cluster.Min(x => x.X) - 5 * characterWidth);
        var right = Math.Min(width, cluster.Max(x => x.X + x.Width) + 13 * characterWidth);
        var top = Math.Max(0, cluster.Min(x => x.Y) - 2 * lineHeight);
        var bottom = Math.Min(height, cluster.Max(x => x.Y + x.Height) + 2 * lineHeight);
        // Do not truncate longer sequence numbers or fractional timestamps to the nominal
        // receipt width. Use nearby numeric words to extend the working area when needed.
        var numericWords = words.Where(x => x.Text.Any(char.IsAsciiDigit) && x.Y >= top && x.Y < bottom
            && x.Height <= 2 * lineHeight && cluster.Any(anchor =>
                Math.Abs((x.Y + x.Height / 2) - (anchor.Y + anchor.Height / 2)) <= lineHeight
                && x.X >= anchor.X - 8 * characterWidth && x.X <= anchor.X + anchor.Width + 20 * characterWidth)).ToArray();
        if (numericWords.Length > 0)
        {
            var padding = Math.Max(characterWidth, ContrastBorder + 4);
            left = Math.Max(0, Math.Min(left, numericWords.Min(x => x.X) - padding));
            right = Math.Min(width, Math.Max(right, numericWords.Max(x => x.X + x.Width) + padding));
        }
        return right - left > 2 * ContrastBorder && bottom - top > 2 * ContrastBorder
            ? new Rect(left, top, right - left, bottom - top) : null;
    }

    private static Rect? FindReceiptColumn(List<RecognizedTimingLine> lines, int width, int height)
    {
        var anchors = lines.Where(x => x.Height is >= 6 and <= 100 && x.Width > 50
            && ReceiptPrefix().IsMatch(x.Text) && x.Text.Count(char.IsAsciiDigit) >= 8).ToArray();
        if (anchors.Length < 6) { return null; }
        var cluster = anchors.Select(anchor => anchors.Where(x =>
            Math.Abs((x.X + x.Width / 2) - (anchor.X + anchor.Width / 2)) <= Math.Min(x.Width, anchor.Width) / 3).ToArray())
            .MaxBy(x => x.Length)!;
        if (cluster.Length < 6) { return null; }
        var lineHeight = cluster.Select(x => x.Height).Order().ElementAt(cluster.Length / 2);
        if (cluster.Max(x => x.Y) - cluster.Min(x => x.Y) < 8 * lineHeight) { return null; }
        var left = Math.Max(0, cluster.Select(x => x.X).Order().ElementAt(cluster.Length / 10) - lineHeight);
        var right = Math.Min(width, cluster.Select(x => x.X + x.Width).Order().ElementAt(cluster.Length / 2) + lineHeight);
        var top = Math.Max(0, cluster.Min(x => x.Y) - 2 * lineHeight);
        var bottom = Math.Min(height, cluster.Max(x => x.Y + x.Height) + 2 * lineHeight);
        return right > left ? new Rect(left, top, right - left, bottom - top) : null;
    }

    private static List<RecognizedTimingLine> ReadReceiptRows(TesseractEngine engine, Pix pixels, Rect column,
        bool useLocalContrast, CancellationToken ct)
    {
        using var normalized = useLocalContrast ? NormalizeReceipt(pixels, column, ct) : null;
        var workingPixels = normalized ?? pixels;
        var workingArea = normalized is null ? column : new Rect(0, 0, normalized.Width, normalized.Height);
        var rows = ReadLines(engine, workingPixels, workingArea, ct);
        if (rows.Count == 0) { return rows; }
        var typicalHeight = rows.Select(x => x.Height).Order().ElementAt(rows.Count / 2);
        using var gray = workingPixels.ConvertTo8(0);
        // Bound memory use: the optional second pass never exceeds 20 megapixels.
        using var enlarged = (long)gray.Width * gray.Height <= 5_000_000 ? gray.Scale(2, 2) : null;
        var result = new List<RecognizedTimingLine>();
        engine.SetVariable("tessedit_char_whitelist", "0123456789CMO:., ");
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var edgeMargin = Math.Max(2, row.Height / 2);
            if (useLocalContrast && (row.X <= (column.X1 == 0 ? edgeMargin : 2)
                || row.X + row.Width >= gray.Width - (column.X2 == pixels.Width ? edgeMargin : 2)
                || row.Y <= (column.Y1 == 0 ? edgeMargin : 2)
                || row.Y + row.Height >= gray.Height - (column.Y2 == pixels.Height ? edgeMargin : 2)))
            {
                result.Add(row with { Text = TimingEvidenceMatching.OcrReviewRequiredPrefix
                    + "Text touches the working image boundary: " + row.Text });
                continue;
            }
            if (row.Height > typicalHeight * 2 || row.Height < 6) { result.Add(row); continue; }
            var margin = Math.Clamp(typicalHeight / 7, 2, 5);
            var top = Math.Max(0, row.Y - margin);
            var bottom = Math.Min(gray.Height, row.Y + row.Height + margin);
            var alternatives = new List<RecognizedTimingLine> { row };
            void ReadRow(Pix source, int scale)
            {
                ct.ThrowIfCancellationRequested();
                var area = new Rect(workingArea.X1 * scale, top * scale,
                    workingArea.Width * scale, (bottom - top) * scale);
                using var cropped = useLocalContrast ? CropGray(source, area, ct) : null;
                using var page = engine.Process(cropped ?? source,
                    cropped is null ? area : new Rect(0, 0, cropped.Width, cropped.Height), PageSegMode.SingleLine);
                alternatives.Add(row with { Text = page.GetText().Trim() });
            }
            ReadRow(gray, 1);
            if (enlarged is not null) { ReadRow(enlarged, 2); }
            // A disagreement belongs to the physical row, even if one alternative lies outside
            // the later matching tolerance. The portable marker keeps that row out of matching.
            var candidates = alternatives.SelectMany(line => TimingEvidenceMatching.ParseLine("row", line.Text, new DateOnly(2000, 1, 1))
                .Select(stamp => (Line: line, Stamp: stamp))).ToArray();
            if (candidates.Length == 0) { result.Add(row); continue; }
            var known = candidates.Where(x => x.Stamp.Channel is not null).ToArray();
            if (candidates.Select(x => (x.Stamp.Ticks, x.Stamp.Precision)).Distinct().Count() > 1
                || known.Select(x => x.Stamp.Channel).Distinct().Count() > 1)
            {
                result.Add(row with { Text = TimingEvidenceMatching.OcrReviewRequiredPrefix
                    + string.Join(" | ", alternatives.Select(x => x.Text).Distinct(StringComparer.Ordinal)) });
                continue;
            }
            // An unknown channel does not contradict an explicit channel on the same unchanged timestamp.
            result.Add((known.Length == 0 ? candidates : known)[0].Line);
        }
        return useLocalContrast
            ? result.Select(row => row with { X = row.X + column.X1 + ContrastBorder,
                Y = row.Y + column.Y1 + ContrastBorder }).ToList()
            : result;
    }

    private static RecognizedTimingLine MarkImageBoundary(RecognizedTimingLine line, int width, int height)
    {
        var margin = Math.Max(2, line.Height / 2);
        return line.X <= margin || line.Y <= margin || line.X + line.Width >= width - margin
            || line.Y + line.Height >= height - margin
            ? line with { Text = TimingEvidenceMatching.OcrReviewRequiredPrefix + "Text touches the image boundary: " + line.Text }
            : line;
    }

    private static Pix NormalizeReceipt(Pix pixels, Rect column, CancellationToken ct)
    {
        using var gray = pixels.ConvertTo8(0);
        using var cropped = CropGray(gray, column, ct);
        // Work on a copy: local thresholding retains faint strokes under uneven lighting,
        // while excluding the surrounding fabric from both segmentation and thresholding.
        // The outer ring supplies thresholding context, not recognized content. The
        // resulting origin is shifted by ContrastBorder on both axes. Large images use
        // tiles to bound accumulator memory, then remove the same contextual ring.
        if ((long)cropped.Width * cropped.Height <= 4_000_000)
        {
            using var binary = cropped.BinarizeSauvola(ContrastWindowRadius, 0.08f, false);
            ct.ThrowIfCancellationRequested();
            return binary.ConvertTo8(0);
        }
        using var tiled = cropped.BinarizeSauvolaTiled(ContrastWindowRadius, 0.08f,
            (cropped.Width + 1023) / 1024, (cropped.Height + 1023) / 1024);
        using var tiledGray = tiled.ConvertTo8(0);
        ct.ThrowIfCancellationRequested();
        return CropGray(tiledGray, new Rect(ContrastBorder, ContrastBorder,
            cropped.Width - 2 * ContrastBorder, cropped.Height - 2 * ContrastBorder), ct);
    }

    private static Pix CropGray(Pix source, Rect area, CancellationToken ct)
    {
        var cropped = Pix.Create(area.Width, area.Height, 8);
        try
        {
            var input = source.GetData();
            var output = cropped.GetData();
            var sourceWords = new int[input.WordsPerLine];
            var targetWords = new int[output.WordsPerLine];
            for (var y = 0; y < area.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                Marshal.Copy(input.Data + (y + area.Y1) * input.WordsPerLine * 4, sourceWords, 0, sourceWords.Length);
                Array.Fill(targetWords, -1);
                for (var x = 0; x < area.Width; x++)
                {
                    var sourceX = x + area.X1;
                    var value = (sourceWords[sourceX / 4] >> (24 - sourceX % 4 * 8)) & 255;
                    var shift = 24 - x % 4 * 8;
                    targetWords[x / 4] = (targetWords[x / 4] & ~(255 << shift)) | (value << shift);
                }
                Marshal.Copy(targetWords, 0, output.Data + y * output.WordsPerLine * 4, targetWords.Length);
            }
            return cropped;
        }
        catch { cropped.Dispose(); throw; }
    }
}
