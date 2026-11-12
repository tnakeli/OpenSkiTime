namespace OpenSkiTime.Persistence;

/// <summary>
/// One schema upgrade of an event series file from version <see cref="From"/> to <c>From + 1</c>. <see cref="Sql"/> runs
/// in the upgrade transaction with foreign key enforcement off, so it may rebuild tables (SQLite cannot alter every column
/// in place). It must leave the file with exactly the schema the next version creates, and it must preserve raw timing
/// input, audit history and approvals unchanged.
/// </summary>
internal sealed record SeriesMigration(int From, string Description, string Sql);

/// <summary>
/// Schema versions of event series files. The version is stored in SQLite's <c>user_version</c>; files written before
/// versioning began (user_version 0) are the release baseline. <see cref="SqliteSeriesFileStore.CreateAsync"/> always
/// creates the current schema directly; <see cref="All"/> upgrades older files to it when they are opened.
/// </summary>
internal static class SeriesMigrations
{
    /// <summary>The first supported release schema: format marker <c>OpenSkiTime.Development/6</c>.</summary>
    public const int Baseline = 1;

    /// <summary>
    /// Upgrades in order: entry i upgrades version Baseline + i. Append a migration whenever the EF model, the history
    /// triggers or the FIS points schema change, and change the code that creates new files in the same commit.
    /// </summary>
    public static readonly SeriesMigration[] All = [];

    public static int Current => Baseline + All.Length;
}
