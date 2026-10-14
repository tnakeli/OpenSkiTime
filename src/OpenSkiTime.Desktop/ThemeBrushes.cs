using Avalonia.Media;

namespace OpenSkiTime.Desktop;

/// <summary>Theme brushes for controls built in code, so they follow the tokens in App.axaml.</summary>
internal static class ThemeBrushes
{
    public static IBrush Accent => Find("AccentBrush", Brushes.RoyalBlue);
    public static IBrush MutedText => Find("MutedTextBrush", Brushes.SlateGray);

    private static IBrush Find(string key, IBrush fallback) =>
        Avalonia.Application.Current?.TryGetResource(key, null, out var value) == true && value is IBrush brush ? brush : fallback;
}
