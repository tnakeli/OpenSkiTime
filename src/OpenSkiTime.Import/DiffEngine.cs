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

                bool isParticipating = IsParticipatingValue(rawValue);

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
                    // We don't carry old bib in CompetitorRef (snapshot-only data);
                    // emit a BibAssignmentDiff regardless — the apply service handles
                    // idempotency via AssignBibUseCase.
                    bibAssignments.Add(new BibAssignmentDiff(
                        existing.Id,
                        existing.LastNameUpper,
                        existing.FirstName,
                        null,
                        bibNumber.Value));
                }
            }
        }

        return new ImportDiff(newCompetitors, bibAssignments, participations, warnings);
    }

    private static bool IsParticipatingValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var v = raw.Trim();
        return v is "1" or "x" or "X" or "true" or "True" or "TRUE" or "yes" or "Yes" or "YES"
            || (!string.IsNullOrEmpty(v) && v != "0" && v != "false" && v != "False" && v != "FALSE"
                && v != "no" && v != "No" && v != "NO");
    }
}
