namespace OpenSkiTime.Domain;

/// <summary>
/// FIS points of one competitor as saved in the event series, with the code of the FIS points list they came from.
/// A null value means the competitor has no points in that discipline on that list.
/// </summary>
public sealed record CompetitorFisPoints(string ListCode, decimal? Dh, decimal? Sg, decimal? Sl, decimal? Gs, decimal? Ac)
{
    public static string DisciplineCode(Discipline discipline) => discipline switch
    {
        Discipline.Slalom => "SL",
        Discipline.GiantSlalom => "GS",
        Discipline.SuperG => "SG",
        Discipline.Downhill => "DH",
        _ => "AC",
    };

    public decimal? For(string disciplineCode) => disciplineCode switch
    {
        "DH" => Dh, "SG" => Sg, "SL" => Sl, "GS" => Gs, "AC" => Ac, _ => null,
    };

    public static CompetitorFisPoints FromList(string listCode, IReadOnlyDictionary<string, decimal>? points)
    {
        decimal? Get(string code) => points is not null && points.TryGetValue(code, out var value) ? value : null;
        return new(listCode, Get("DH"), Get("SG"), Get("SL"), Get("GS"), Get("AC"));
    }

    public CompetitorFisPoints Validated()
    {
        if (string.IsNullOrWhiteSpace(ListCode) || ListCode.Trim().Length > 20)
        { throw new DomainValidationException("FIS points need the code of their points list."); }
        foreach (var value in new[] { Dh, Sg, Sl, Gs, Ac })
        {
            if (value is { } points && (points < 0 || points > 9999.99m || decimal.Round(points, 2) != points))
            { throw new DomainValidationException("FIS points must be between 0.00 and 9999.99 with at most two decimals."); }
        }
        return this with { ListCode = ListCode.Trim() };
    }
}

/// <summary>The FIS points list whose points the event series stores: identity, validity and penalty rules.</summary>
public sealed record FisPointsListInfo(string Code, string Name, DateOnly ValidFrom, DateOnly ValidTo,
    FisPenaltyListRules? PenaltyRules = null)
{
    public PointsListSource ToSource() => new(Code, ValidFrom, ValidTo, PenaltyRules);

    public FisPointsListInfo Validated()
    {
        if (string.IsNullOrWhiteSpace(Code) || Code.Trim().Length > 20 || Name.Length > 200 || ValidTo < ValidFrom)
        { throw new DomainValidationException("The FIS points list needs a code, a name and a valid date range."); }
        return this with { Code = Code.Trim() };
    }
}
