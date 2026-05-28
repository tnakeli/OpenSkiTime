namespace OpenSkiTime.Import;

/// <summary>
/// A single data row after header mapping — field values keyed by
/// <see cref="ImportField"/>. Participation columns are stored under
/// <see cref="ImportField.ParticipationFlag"/> per short label in
/// <see cref="Participations"/>.
/// </summary>
public sealed class RawImportRow
{
    private readonly Dictionary<ImportField, string> _fields;

    public RawImportRow(
        Dictionary<ImportField, string> fields,
        Dictionary<string, string> participations)
    {
        _fields = fields ?? throw new ArgumentNullException(nameof(fields));
        Participations = participations ?? throw new ArgumentNullException(nameof(participations));
    }

    /// <summary>
    /// Participation columns keyed by competition ShortLabel.
    /// Value is the raw cell string.
    /// </summary>
    public IReadOnlyDictionary<string, string> Participations { get; }

    public string? Get(ImportField field)
        => _fields.TryGetValue(field, out var v) ? v : null;

    public bool TryGet(ImportField field, out string value)
    {
        if (_fields.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v))
        {
            value = v.Trim();
            return true;
        }

        value = string.Empty;
        return false;
    }
}
