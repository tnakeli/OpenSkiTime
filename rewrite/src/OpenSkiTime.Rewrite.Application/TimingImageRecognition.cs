namespace OpenSkiTime.Rewrite.Application;

public sealed record RecognizedTimingLine(string Text, int X, int Y, int Width, int Height);
public sealed record TimingImageText(string Engine, IReadOnlyList<RecognizedTimingLine> Lines);

// Recognition proposes text; only the operator can verify timing evidence.
public interface ITimingImageRecognizer
{
    Task<TimingImageText> RecognizeAsync(byte[] image, CancellationToken ct = default);
}
