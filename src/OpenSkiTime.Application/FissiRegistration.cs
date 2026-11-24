namespace OpenSkiTime.Application;

public enum FissiRaceStatus
{
    Ready, DateDiffers, NoneRegistered, NoCodex, NotFound, RaceDiffers, SeveralMatches, StartListSaved,
}

/// <summary>A race of regs.fissi.fi. Discipline and gender (M or W) are empty when the service does not tell them.</summary>
public sealed record FissiRace(string Codex, DateOnly Date, string Discipline, string Gender);

/// <summary>A registered athlete; the name is one text ("First name SURNAME").</summary>
public sealed record FissiAthlete(string? Code, string FullName, int? BirthYear, string? Nation, string? Club);

public sealed record FissiRegistrations(FissiRace Race, IReadOnlyList<FissiAthlete> Athletes);

/// <summary>The registrations found for a competition; they are present only when the race is the one of the competition.</summary>
public sealed record FissiCompetition(Guid CompetitionId, string? Codex, FissiRaceStatus Status, FissiRegistrations? Registrations = null);
