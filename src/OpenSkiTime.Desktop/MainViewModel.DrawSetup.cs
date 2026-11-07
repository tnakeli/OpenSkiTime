using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

/// <summary>One row of the start order setup grid: a start block with the options of its first and later runs.</summary>
public sealed partial class DrawBlockRow : ObservableObject
{
    public static IReadOnlyList<string> FirstRunChoices { get; } = ["Draw all", "By points"];
    public static IReadOnlyList<string> BetterPointsChoices { get; } = ["Lower", "Higher"];
    public static IReadOnlyList<string> LaterRunChoices { get; } = ["Reverse start order", "Reverse results", "Same order"];
    public static IReadOnlyList<string> NonFinisherChoices { get; } = ["Start last", "Do not start"];

    public DrawBlockRow(IEnumerable<string> classes, int starters, string seed = "")
    {
        Classes = [.. classes];
        Starters = starters;
        Seed = seed;
    }

    public IReadOnlyList<string> Classes { get; }
    public string Seed { get; set; }
    public int Starters { get; }
    public string ClassesText => string.Join(" + ", Classes);
    [ObservableProperty] private int _order;
    [ObservableProperty] private string _firstRun = FirstRunChoices[0];
    [ObservableProperty] private string _betterPoints = BetterPointsChoices[0];
    [ObservableProperty] private int _firstGroup = 15;
    [ObservableProperty] private bool _zeroIsNoPoints;
    [ObservableProperty] private string _laterRun = LaterRunChoices[0];
    // A number, or "All".
    [ObservableProperty] private string _reverseFirst = "30";
    [ObservableProperty] private string _nonFinishers = NonFinisherChoices[0];
    [ObservableProperty] private string _firstBib = string.Empty;
    // Worked out from the order, the starters of every block, the draw settings and the first bibs typed in other rows.
    [ObservableProperty] private string _autoFirstBib = string.Empty;
    [ObservableProperty] private string _bibRange = string.Empty;
    [ObservableProperty] private bool _bibConflict;

    public bool IsByPoints => FirstRun == FirstRunChoices[1];
    public bool IsReverseResults => LaterRun == LaterRunChoices[1];
    partial void OnFirstRunChanged(string value) => OnPropertyChanged(nameof(IsByPoints));
    partial void OnLaterRunChanged(string value) => OnPropertyChanged(nameof(IsReverseResults));

    public StartBlock ToBlock()
    {
        int? bib = null;
        if (FirstBib.Trim().Length > 0)
        {
            if (!int.TryParse(FirstBib.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 99999)
            { throw new DomainValidationException($"First bib of {ClassesText} must be a number from 1 to 99999, or empty."); }
            bib = parsed;
        }
        int? count = 30;
        var reverse = ReverseFirst.Trim();
        if (reverse.Equals("All", StringComparison.OrdinalIgnoreCase) || reverse.Length == 0) { count = null; }
        else if (!int.TryParse(reverse, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedCount) || parsedCount < 1)
        { throw new DomainValidationException($"Reverse first of {ClassesText} must be a number or All."); }
        else { count = parsedCount; }
        return new(ClassesText, Classes,
            new(IsByPoints ? FirstRunKind.ByPoints : FirstRunKind.DrawAll, Math.Max(1, FirstGroup),
                BetterPoints == BetterPointsChoices[1] ? PointsDirection.HigherIsBetter : PointsDirection.LowerIsBetter, ZeroIsNoPoints),
            new(LaterRun == LaterRunChoices[1] ? LaterRunKind.ReverseResults : LaterRun == LaterRunChoices[2] ? LaterRunKind.SameOrder : LaterRunKind.ReverseStartOrder,
                count, NonFinishers == NonFinisherChoices[1] ? OpenSkiTime.Domain.NonFinishers.DoNotStart : OpenSkiTime.Domain.NonFinishers.StartLast),
            Seed, bib);
    }

    public static DrawBlockRow From(StartBlock block, int starters) => new((block ?? throw new ArgumentNullException(nameof(block))).Classes, starters, block.Seed)
    {
        FirstRun = block.FirstRun.Kind == FirstRunKind.ByPoints ? FirstRunChoices[1] : FirstRunChoices[0],
        BetterPoints = block.FirstRun.Direction == PointsDirection.HigherIsBetter ? BetterPointsChoices[1] : BetterPointsChoices[0],
        FirstGroup = block.FirstRun.FirstGroupSize,
        ZeroIsNoPoints = block.FirstRun.ZeroIsNoPoints,
        LaterRun = block.LaterRun.Kind switch { LaterRunKind.ReverseResults => LaterRunChoices[1], LaterRunKind.SameOrder => LaterRunChoices[2], _ => LaterRunChoices[0] },
        ReverseFirst = block.LaterRun.ReverseCount?.ToString(CultureInfo.InvariantCulture) ?? "All",
        NonFinishers = block.LaterRun.NonFinishers == OpenSkiTime.Domain.NonFinishers.DoNotStart ? NonFinisherChoices[1] : NonFinisherChoices[0],
        FirstBib = block.FirstBib?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

public sealed partial class MainViewModel
{
    public IReadOnlyList<string> DrawFieldChoices { get; } = ["All", "Men", "Women"];
    public ObservableCollection<DrawBlockRow> DrawBlocks { get; } = [];
    [ObservableProperty] private DrawBlockRow? _selectedDrawBlock;
    [ObservableProperty] private bool _isDrawSetupOpen = true;
    private string _drawField = "All";
    private StartListRevision? _runOneList;
    private IReadOnlyDictionary<Guid, string> _drawClasses = new Dictionary<Guid, string>();

    public string DrawField
    {
        get => _drawField;
        set
        {
            if (value == _drawField || value is null) { return; }
            if (!_settingDraw && !CanLeaveDrawInput()) { OnPropertyChanged(); return; }
            SetProperty(ref _drawField, value);
            if (!_settingDraw) { _ = LoadDrawAsync(); }
        }
    }

    // Club races use the flexible start order; FIS competitions keep the FIS draw, and so does a race that was a FIS
    // competition when its lists were drawn.
    private bool _drawFlex;
    public bool IsFlexDraw => _drawFlex;
    public bool IsFisDrawLayout => !IsFlexDraw;
    public bool CanEditDrawSetup => IsFlexDraw && !IsDrawBusy && !DrawRunStarted && DrawRevision?.HasCapture != true;
    public bool ShowFisDrawPoints => !IsFlexDraw && ShowDrawPoints;
    public bool ShowFlexDrawPoints => IsFlexDraw && ShowDrawPoints;
    public bool ShowDrawClass => IsFlexDraw;
    public bool CanChooseDrawField => IsFlexDraw && IsFirstDrawRun && DrawRevision is null;
    public string DrawSettingsHeader => IsFlexDraw ? "Draw settings" : "Draw settings · FIS standard";
    public bool ShowFisReversePicker => IsLaterDrawRun && IsFisDrawLayout;
    public string DrawInputButtonText => $"Run {DrawRun - 1} input";
    public string DrawSetupTitle => DrawRun == 1 ? "Start order · first run" : $"Start order · run {DrawRun}";
    private Gender? DrawFieldGender => _drawField switch { "Men" => Gender.Male, "Women" => Gender.Female, _ => null };

    private string FlexDrawBlockReason => DrawBlocks.Count == 0 ? "There are no starters to order."
        : DrawRun > 1 && _sourceRun is null ? $"Draw Run {DrawRun - 1} before preparing Run {DrawRun}."
        : DrawRun > 1 && !IsRunAvailable(_drawDesk ?? new(0, []), DrawRun) ? $"Run {DrawRun - 1} has not started yet."
        : DrawRun > 1 && _sourceTiming is { Complete: false } && DrawBlocks.Any(x => x.IsReverseResults)
            ? $"Run {DrawRun - 1} is incomplete. Finish or classify every starter in Timing, or order the blocks by start order."
        : string.Empty;

    private static Dictionary<Guid, string> ResolveDrawClasses(CompetitorDeskDetails desk, IEnumerable<CompetitorDetails> entrants, out string issue)
    {
        var classes = new Dictionary<Guid, string>();
        var bad = new List<string>();
        foreach (var entrant in entrants)
        {
            var label = desk.Categories.Count == 0 ? "All" : CategoryResolver.Resolve(entrant.Values, desk.Categories);
            if (label is CategoryResolver.Unclassified or "Ambiguous") { bad.Add($"{entrant.Values.Surname} {entrant.Values.FirstName} ({label.ToLowerInvariant()})".Trim()); }
            classes[entrant.Id] = label;
        }
        issue = bad.Count == 0 ? string.Empty
            : "Every starter needs exactly one class. Check the birth year, gender and category rules in Competitors for: "
                + string.Join(", ", bad.Order(StringComparer.CurrentCulture).Take(8)) + (bad.Count > 8 ? $" and {bad.Count - 8} more." : ".");
        return classes;
    }

    // The setup grid: the saved setup of this list, else the setup of the run before it, else one block per class.
    private void BuildDrawSetup(IReadOnlyCollection<CompetitorDetails> entrants, CompetitorDeskDetails desk, StartListRevision? saved)
    {
        DrawBlocks.Clear();
        var counts = entrants.Where(x => _drawClasses.ContainsKey(x.Id)).GroupBy(x => _drawClasses[x.Id]).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var template = saved?.Plan.Setup ?? (DrawRun > 1 ? _sourceRun?.Plan.Setup : null);
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (template is not null)
        {
            foreach (var block in template.Blocks)
            {
                var present = block.Classes.Where(counts.ContainsKey).ToArray();
                if (present.Length == 0 && saved is null) { continue; }
                var row = DrawBlockRow.From(block with { Classes = present.Length == 0 ? block.Classes : present }, present.Sum(x => counts[x]));
                if (saved is null && DrawRun == 1) { row.Seed = string.Empty; }
                if (saved is null && DrawRun > 1) { row.Seed = string.Empty; }
                DrawBlocks.Add(row);
                foreach (var c in present) { covered.Add(c); }
            }
        }
        var order = desk.Categories.Select((x, i) => (x.Values.Label, i)).ToDictionary(x => x.Label, x => x.i, StringComparer.OrdinalIgnoreCase);
        foreach (var className in counts.Keys.Where(x => !covered.Contains(x)).OrderBy(x => order.GetValueOrDefault(x, int.MaxValue)).ThenBy(x => x, StringComparer.CurrentCulture))
        { DrawBlocks.Add(new([className], counts[className])); }
        RenumberDrawBlocks();
    }

    private readonly HashSet<DrawBlockRow> _trackedDrawBlocks = [];

    private void RenumberDrawBlocks()
    {
        for (var i = 0; i < DrawBlocks.Count; i++)
        {
            DrawBlocks[i].Order = i + 1;
            if (_trackedDrawBlocks.Add(DrawBlocks[i]))
            { DrawBlocks[i].PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DrawBlockRow.FirstBib)) { RecomputeDrawBibs(); } }; }
        }
        RecomputeDrawBibs();
        OnPropertyChanged(nameof(CanPrepareDraw)); OnPropertyChanged(nameof(DrawBlockReason));
    }

    partial void OnDrawFirstBibChanged(int value) => RecomputeDrawBibs();
    partial void OnDrawSkippedBibsTextChanged(string value) => RecomputeDrawBibs();

    // Shows the bib range of every block. A block starts after the one before it, at the first bib of the draw settings
    // for the first block, leaving out the skipped bibs; a first bib typed in a row moves that row and all after it.
    private void RecomputeDrawBibs()
    {
        IReadOnlyCollection<int> skipped;
        try { skipped = FisStartOrder.ParseBibList(DrawSkippedBibsText); }
        catch (DomainValidationException) { skipped = []; }
        var next = Math.Max(1, DrawFirstBib);
        foreach (var row in DrawBlocks)
        {
            row.BibConflict = false;
            if (row.Starters == 0) { row.AutoFirstBib = string.Empty; row.BibRange = string.Empty; continue; }
            var start = next;
            if (int.TryParse(row.FirstBib.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var own) && own >= 1)
            {
                row.BibConflict = own < next;
                start = own;
            }
            try
            {
                var bibs = FisStartOrder.Bibs(start, skipped, row.Starters);
                row.AutoFirstBib = FisStartOrder.Bibs(next, skipped, 1)[0].ToString(CultureInfo.InvariantCulture);
                row.BibRange = row.BibConflict ? $"{bibs[0]}–{bibs[^1]} overlaps" : $"{bibs[0]}–{bibs[^1]}";
                next = Math.Max(next, bibs[^1] + 1);
            }
            catch (DomainValidationException) { row.AutoFirstBib = string.Empty; row.BibRange = "Over 99999"; row.BibConflict = true; }
        }
    }

    [RelayCommand]
    private void MoveDrawBlockUp() => MoveDrawBlock(-1);
    [RelayCommand]
    private void MoveDrawBlockDown() => MoveDrawBlock(1);

    private void MoveDrawBlock(int step)
    {
        if (SelectedDrawBlock is not { } row || !CanEditDrawSetup) { return; }
        var index = DrawBlocks.IndexOf(row);
        var target = index + step;
        if (index < 0 || target < 0 || target >= DrawBlocks.Count) { return; }
        DrawBlocks.Move(index, target);
        SelectedDrawBlock = row;
        RenumberDrawBlocks();
    }

    // Draws the selected block together with the one below it as one group.
    [RelayCommand]
    private void MergeDrawBlock()
    {
        if (SelectedDrawBlock is not { } row || !CanEditDrawSetup) { return; }
        var index = DrawBlocks.IndexOf(row);
        if (index < 0 || index >= DrawBlocks.Count - 1) { SetStatus("Select a block that has another block below it to merge with.", error: true); return; }
        var next = DrawBlocks[index + 1];
        var merged = new DrawBlockRow(row.Classes.Concat(next.Classes), row.Starters + next.Starters) { FirstRun = row.FirstRun, BetterPoints = row.BetterPoints,
            FirstGroup = row.FirstGroup, ZeroIsNoPoints = row.ZeroIsNoPoints, LaterRun = row.LaterRun, ReverseFirst = row.ReverseFirst,
            NonFinishers = row.NonFinishers, FirstBib = row.FirstBib };
        DrawBlocks.RemoveAt(index + 1);
        DrawBlocks[index] = merged;
        SelectedDrawBlock = merged;
        RenumberDrawBlocks();
    }

    // Separates a merged block into one block per class, each with the options of the merged block.
    [RelayCommand]
    private void SplitDrawBlock()
    {
        if (SelectedDrawBlock is not { Classes.Count: > 1 } row || !CanEditDrawSetup) { return; }
        var index = DrawBlocks.IndexOf(row);
        var counts = _drawClasses.Values.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        DrawBlocks.RemoveAt(index);
        var at = index;
        foreach (var className in row.Classes)
        {
            DrawBlocks.Insert(at, new([className], counts.GetValueOrDefault(className)) { FirstBib = at++ == index ? row.FirstBib : string.Empty, FirstRun = row.FirstRun, BetterPoints = row.BetterPoints,
                FirstGroup = row.FirstGroup, ZeroIsNoPoints = row.ZeroIsNoPoints, LaterRun = row.LaterRun, ReverseFirst = row.ReverseFirst,
                NonFinishers = row.NonFinishers });
        }
        RenumberDrawBlocks();
    }

    // Gives every block the options of the selected block.
    [RelayCommand]
    private void ApplyDrawBlockToAll()
    {
        if (SelectedDrawBlock is not { } row || !CanEditDrawSetup) { return; }
        foreach (var other in DrawBlocks.Where(x => !ReferenceEquals(x, row)))
        {
            other.FirstRun = row.FirstRun; other.BetterPoints = row.BetterPoints; other.FirstGroup = row.FirstGroup;
            other.ZeroIsNoPoints = row.ZeroIsNoPoints; other.LaterRun = row.LaterRun; other.ReverseFirst = row.ReverseFirst;
            other.NonFinishers = row.NonFinishers;
        }
        SetStatus("Options of the selected block applied to every block.");
    }

    // Draws only the selected block again; the other blocks keep their seeds and so their order.
    [RelayCommand]
    private async Task DrawSelectedBlockAgainAsync()
    {
        if (SelectedDrawBlock is not { } row) { SetStatus("Select a block first.", error: true); return; }
        if (DrawRun != 1 || DrawRevision is null) { SetStatus("Draw the first run before drawing one block again.", error: true); return; }
        foreach (var other in DrawBlocks.Where(x => !ReferenceEquals(x, row)).Where(x => x.Seed.Length == 0)) { other.Seed = NewDrawSeed(); }
        row.Seed = NewDrawSeed();
        _keepDrawSeeds = true;
        await PrepareDrawAsync();
    }

    private static string NewDrawSeed() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private StartOrderSetup ReadDrawSetup(bool newSeeds)
    {
        var blocks = new List<StartBlock>();
        foreach (var row in DrawBlocks)
        {
            if (newSeeds || (DrawRun == 1 && row.Seed.Length == 0)) { row.Seed = NewDrawSeed(); }
            blocks.Add(row.ToBlock());
        }
        return new(blocks);
    }

    private async Task PrepareFlexDrawAsync(CompetitionDetails competition, bool drawAllAgain)
    {
        var desk = await workspace.ReadCompetitorDeskAsync();
        if (desk.Revision != _drawDesk!.SeriesRevision) { throw new SeriesConflictException(); }
        var ids = desk.Participations.Where(x => x.CompetitionId == competition.Id && x.Participates).Select(x => x.CompetitorId).ToHashSet();
        var field = DrawFieldGender;
        var starters = desk.Competitors.Where(x => ids.Contains(x.Id) && (field is null || x.Values.Gender == field)).ToArray();
        var classes = ResolveDrawClasses(desk, starters, out var issue);
        if (issue.Length > 0) { throw new DomainValidationException(issue); }
        var setup = ReadDrawSetup(drawAllAgain && DrawRun == 1);
        StartListPlan plan;
        if (DrawRun == 1)
        {
            var discipline = CompetitorFisPoints.DisciplineCode(competition.Values.Discipline);
            var entrants = starters.Select(x => new DrawEntrant(x.Id, x.Values, desk.FisPointsOf(x.Id)?.For(discipline), classes[x.Id])).ToArray();
            var listCode = entrants.Where(x => x.Points is not null).Select(x => desk.FisPointsOf(x.CompetitorId)!.ListCode)
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key;
            var list = listCode is null ? null : desk.PointsList(listCode);
            if (setup.UsesPoints && list is null)
            { throw new DomainValidationException("No points are saved for the starters. Enter Points in Competitors, or use Update from FIS list, then Save."); }
            plan = FlexStartOrder.FirstRun(competition.Id, competition.Values, field, entrants, list?.ToSource() ?? PointsListSource.None,
                new(15, 30, DrawFirstBib, SkippedBibsOrNull()), setup);
        }
        else
        {
            var source = _sourceRun ?? throw new DomainValidationException($"Draw Run {DrawRun - 1} before preparing Run {DrawRun}.");
            IReadOnlyList<RunFinish> results = [];
            if (_sourceTiming is { Complete: true } timing) { results = timing.ToRunFinishes(); }
            else if (setup.UsesResults(DrawRun)) { results = DrawResults.Select(x => x.Read()).ToArray(); }
            plan = FlexStartOrder.LaterRun(source, results, setup);
        }
        await workspace.SaveStartListAsync(new(plan, _drawDesk.SeriesRevision, Environment.UserName,
            DrawRun == 1 ? "Draw" : $"Run {DrawRun} start order", DateTimeOffset.UtcNow, _sourceTimingVersion));
        await LoadDrawAsync();
        SetStatus("Start list saved.");
    }
}
