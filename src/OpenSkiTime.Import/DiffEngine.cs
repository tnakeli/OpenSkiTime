using System.Globalization;
using OpenSkiTime.Application.Abstractions;

namespace OpenSkiTime.Import;

/// <summary>
/// Computes an <see cref="ImportDiff"/> by comparing parsed import rows
/// against the current <see cref="EventSeriesSnapshot"/>.
/// Pure function — no I/O, no side effects.
/// </summary>
public static class DiffEngine
{
    public static ImportDiff Compute(
        EventSeriesSnapshot snapshot,
        IReadOnlyList<RawImportRow> rows)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rows);

        var matcher = new CompetitorMatcher(snapshot.Competitors);

        var newCompetitors = new List<NewCompetitorDiff>();
        var bibAssignments = new List<BibAssignmentDiff>();
        var participations = new List<ParticipationDiff>();
        var fieldChanges = new List<FieldChangeDiff>();
        var warnings = new List<ImportRowWarning>();

        // Existing participation index: (competitorId, competitionId) → IsParticipating
        var existingParticipations = snapshot.Participations
            .ToDictionary(p => (p.CompetitorId, p.CompetitionId), p => p.IsParticipating);

        // Existing bib index: competitorId → bib (from snapshot, if we had it stored)
        // (snapshot CompetitorRef doesn't currently carry bib — that's ok, bib diffs
        //  are driven entirely from the import row's BibNumber column)

        for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
        {
            var row = rows[rowIdx];

            // ── Name resolution ────────────────────────────────────────────
            var (lastRaw, firstName) = NameProjector.Project(row);
            if (lastRaw is null)
            {
                warnings.Add(new ImportRowWarning(rowIdx + 2,
                    "Row has no recognisable last name or full name column — skipped."));
                continue;
            }

            var lastNameUpper = lastRaw.Trim().ToUpper(CultureInfo.InvariantCulture);
            firstName ??= string.Empty;

            // ── Year of birth ──────────────────────────────────────────────
            int yearOfBirth = 0;
            if (row.TryGet(ImportField.YearOfBirth, out var yobRaw))
            {
                if (!int.TryParse(yobRaw, out yearOfBirth) ||
                    yearOfBirth < 1900 || yearOfBirth > DateTime.UtcNow.Year)
                {
                    warnings.Add(new ImportRowWarning(rowIdx + 2,
                        $"Invalid year of birth '{yobRaw}' for {lastNameUpper} {firstName} — skipped."));
                    continue;
                }
            }
            else
            {
                warnings.Add(new ImportRowWarning(rowIdx + 2,
                    $"Missing year of birth for {lastNameUpper} {firstName} — skipped."));
                continue;
            }

            row.TryGet(ImportField.FisCode, out var fisCode);
            row.TryGet(ImportField.NationCode, out var nationCode);
            row.TryGet(ImportField.ClubName, out var clubName);

            // ── Bib ────────────────────────────────────────────────────────
            int? bibNumber = null;
            if (row.TryGet(ImportField.BibNumber, out var bibRaw))
            {
                if (int.TryParse(bibRaw, out var bib) && bib >= 1)
                {
                    bibNumber = bib;
                }
                else
                {
                    warnings.Add(new ImportRowWarning(rowIdx + 2,
                        $"Invalid bib '{bibRaw}' for {lastNameUpper} {firstName} — ignored."));
                }
            }

            // ── Match to existing ──────────────────────────────────────────
            var existing = matcher.Match(lastNameUpper, firstName, yearOfBirth,
                string.IsNullOrWhiteSpace(fisCode) ? null : fisCode);

            // ── Participation flags ────────────────────────────────────────
            var participatingIn = new List<string>();
            foreach (var (shortLabel, rawValue) in row.Participations)
            {
                var competitionRef = snapshot.Competitions
                    .FirstOrDefault(c => string.Equals(
                        c.ShortLabel, shortLabel, StringComparison.OrdinalIgnoreCase));
                if (competitionRef is null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    continue;
                }

                bool isParticipating = ParticipationValueMatcher.IsParticipating(rawValue);

                if (existing is not null)
                {
                    var key = (existing.Id, competitionRef.Id);
                    bool currentlyParticipating = existingParticipations.TryGetValue(key, out var cur) && cur;

                    if (isParticipating != currentlyParticipating)
                    {
                        participations.Add(new ParticipationDiff(
                            existing.Id,
                            existing.LastNameUpper,
                            existing.FirstName,
                            competitionRef.Id,
                            shortLabel,
                            isParticipating));
                    }
                }
                else if (isParticipating)
                {
                    participatingIn.Add(shortLabel);
                }
            }

            if (existing is null)
            {
                // New competitor.
                newCompetitors.Add(new NewCompetitorDiff(
                    lastNameUpper,
                    firstName,
                    yearOfBirth,
                    string.IsNullOrWhiteSpace(fisCode) ? null : fisCode.Trim(),
                    string.IsNullOrWhiteSpace(nationCode) ? null : nationCode.Trim(),
                    string.IsNullOrWhiteSpace(clubName) ? null : clubName.Trim(),
                    bibNumber,
                    participatingIn));
            }
            else
            {
                // Existing competitor — check bib assignment.
                if (bibNumber.HasValue)
                {
                    bibAssignments.Add(new BibAssignmentDiff(
                        existing.Id,
                        existing.LastNameUpper,
                        existing.FirstName,
                        null,
                        bibNumber.Value));
                }

                // Check scalar field changes.
                void CheckField(string fieldName, string? importedRaw, string? currentValue)
                {
                    if (string.IsNullOrWhiteSpace(importedRaw))
                    {
                        return;
                    }

                    var imported = importedRaw.Trim();
                    var current = currentValue ?? string.Empty;
                    if (!string.Equals(imported, current, StringComparison.OrdinalIgnoreCase))
                    {
                        fieldChanges.Add(new FieldChangeDiff(existing.Id, fieldName, current, imported));
                    }
                }

                // Only update LastName/FirstName when matched by FIS Code
                // (to avoid overwriting the key used to find the competitor).
                bool matchedByFis = !string.IsNullOrWhiteSpace(fisCode)
                    && string.Equals(existing.Code, fisCode?.Trim(), StringComparison.OrdinalIgnoreCase);

                if (matchedByFis)
                {
                    CheckField("LastName",  lastNameUpper, existing.LastNameUpper);
                    CheckField("FirstName", firstName,     existing.FirstName);
                }

                if (row.TryGet(ImportField.YearOfBirth, out var importedYob)
                    && int.TryParse(importedYob, out var newYob)
                    && newYob != existing.YearOfBirth)
                {
                    fieldChanges.Add(new FieldChangeDiff(
                        existing.Id, "YearOfBirth",
                        existing.YearOfBirth.ToString(CultureInfo.InvariantCulture),
                        newYob.ToString(CultureInfo.InvariantCulture)));
                }

                if (row.TryGet(ImportField.Gender, out var importedGender))
                {
                    CheckField("Gender", importedGender, existing.Gender);
                }

                if (row.TryGet(ImportField.FisCode, out var importedFis))
                {
                    CheckField("FisCode", importedFis, existing.Code);
                }

                if (row.TryGet(ImportField.NationCode, out var importedNation))
                {
                    CheckField("NationCode", importedNation, existing.NationCode);
                }

                if (row.TryGet(ImportField.ClubName, out var importedClub))
                {
                    CheckField("ClubName", importedClub, existing.ClubName);
                }
            }
        }

        return new ImportDiff(newCompetitors, bibAssignments, participations, fieldChanges, warnings);
    }

}
