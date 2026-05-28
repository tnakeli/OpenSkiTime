namespace OpenSkiTime.Import;

/// <summary>
/// Converts tokenized rows + column mappings into <see cref="RawImportRow"/> objects.
/// </summary>
public static class RowParser
{
    public static IReadOnlyList<RawImportRow> Parse(
        IReadOnlyList<string[]> rows,
        IReadOnlyList<ColumnMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(mappings);

        if (rows.Count < 2)
        {
            return Array.Empty<RawImportRow>();
        }

        var result = new List<RawImportRow>(rows.Count - 1);

        // rows[0] is the header — skip it.
        for (int r = 1; r < rows.Count; r++)
        {
            var cells = rows[r];
            var fields = new Dictionary<ImportField, string>();
            var participations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mapping in mappings)
            {
                if (mapping.Index >= cells.Length)
                {
                    continue;
                }

                var cell = cells[mapping.Index].Trim();

                if (mapping.Field == ImportField.ParticipationFlag)
                {
                    participations[mapping.RawHeader] = cell;
                }
                else if (mapping.Field != ImportField.Unknown)
                {
                    // Last writer wins if same field appears twice (shouldn't happen).
                    fields[mapping.Field] = cell;
                }
            }

            result.Add(new RawImportRow(fields, participations));
        }

        return result;
    }
}
