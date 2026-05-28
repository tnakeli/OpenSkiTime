namespace OpenSkiTime.Import;

/// <summary>
/// Splits raw pasted or file-sourced text into a rectangular grid of
/// string cells. Handles tab-delimited rows with optional double-quote
/// quoting (RFC 4180-style, but with tab as delimiter).
/// Empty trailing columns on a row are included so every row has the
/// same column count as the header row.
/// </summary>
public static class TsvTokenizer
{
    /// <summary>
    /// Parses <paramref name="text"/> and returns rows as string arrays.
    /// The first row is the header. Blank lines are skipped.
    /// </summary>
    public static IReadOnlyList<string[]> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string[]>();
        }

        var lines = text.Split('\n');
        var result = new List<string[]>(lines.Length);

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            result.Add(SplitLine(line));
        }

        if (result.Count == 0)
        {
            return Array.Empty<string[]>();
        }

        // Normalise: pad shorter rows to header width.
        int width = result[0].Length;
        for (int i = 1; i < result.Count; i++)
        {
            if (result[i].Length < width)
            {
                var padded = new string[width];
                Array.Copy(result[i], padded, result[i].Length);
                for (int j = result[i].Length; j < width; j++)
                {
                    padded[j] = string.Empty;
                }

                result[i] = padded;
            }
        }

        return result;
    }

    private static string[] SplitLine(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuote)
            {
                if (c == '"')
                {
                    // Peek: escaped quote?
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuote = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuote = true;
                }
                else if (c == '\t')
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}
