using System.IO.Compression;
using System.Text;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class FisPointsListTests
{
    [Fact]
    public void ReadsHeaderCompetitorAndPointsWithoutLosingCodeJoin()
    {
        var bytes = Archive(
            "1\tAL\t123456\tNORD\tAda\tW\t2002-02-03\tFIN\t\tNorth Club\t\tA\n",
            "7\t465\t1\tSL\t12.34\t2\t\t2026-09-22\n");
        var list = FisPointsListReader.Read(bytes);
        Assert.Equal("1327: 13th FIS points list 2026/27 (22-09-2026)", list.DisplayName);
        var athlete = Assert.Single(list.Search("123456"));
        Assert.Equal(Gender.Female, athlete.Gender);
        Assert.Equal(2002, athlete.BirthYear);
        Assert.Equal(12.34m, athlete.Points!["SL"]);
        Assert.Equal("123456", Assert.Single(list.Search("nord")).Code);
    }

    [Fact]
    public void RejectsDuplicateCodesAndMismatchedPointsList()
    {
        var duplicate = Archive(
            "1\tAL\t123456\tNORD\tAda\tW\t2002-02-03\tFIN\t\tNorth Club\t\tA\n"
            + "2\tAL\t123456\tWEST\tEli\tM\t2001-01-01\tFIN\t\tWest Club\t\tA\n",
            "7\t465\t1\tSL\t12.34\t2\t\t2026-09-22\n");
        Assert.Throws<DomainValidationException>(() => FisPointsListReader.Read(duplicate));
        var mismatched = Archive(
            "1\tAL\t123456\tNORD\tAda\tW\t2002-02-03\tFIN\t\tNorth Club\t\tA\n",
            "7\t999\t1\tSL\t12.34\t2\t\t2026-09-22\n");
        Assert.Throws<DomainValidationException>(() => FisPointsListReader.Read(mismatched));
    }

    private static byte[] Archive(string competitors, string points)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "AL1327hdr.csv", "Listid\tSeasoncode\tListnumber\tListname\tCalculationdate\tStartracedate\tEndracedate\tValidfrom\tValidto\tLastupdate\n"
                + "465\t2027\t13\t13th FIS points list 2026/27\t2026-09-22\t2026-07-01\t2026-09-20\t2026-09-24\t2026-09-30\t2026-09-22 04:19:38\n");
            Add(zip, "AL1327com.csv", "Competitorid\tSectorcode\tFiscode\tLastname\tFirstname\tGender\tBirthdate\tNationcode\tNationalcode\tSkiclub\tAssociation\tStatus\n" + competitors);
            Add(zip, "AL1327pts.csv", "Recid\tListid\tCompetitorid\tDisciplinecode\tFispoints\tPosition\tPenalty\tLastupdate\n" + points);
        }
        return output.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string contents)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuleTablesAreJoinedByListCategoryGenderAndRaceLevel(bool wrongList)
    {
        var bytes = Archive("1\tAL\t990001\tTEST\tSynthetic\tW\t2002-02-03\tFIN\t\tTest Club\t\tA\n", "");
        using var output = new MemoryStream(); output.Write(bytes);
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, leaveOpen: true))
        {
            Add(zip, "AL1327cat.csv", "Recid\tListid\tSeasoncode\tCatcode\tMinfispoints\tMaxfispoints\tLastupdate\n"
                + $"1\t{(wrongList ? 999 : 465)}\t2027\tTEST\t29.00\t888.00\t2026-09-22\n");
            Add(zip, "AL1327dis.csv", "Recid\tListid\tSeasoncode\tDisciplinecode\tGender\tZvalue\tFvalue\tMaxpoints\tAdder0\tAdder1\tAdder2\tAdder3\tAdder4\n"
                + "1\t465\t2027\tSL\tW\t0.00\t731\t166\t0\t1\t2\t7\t9\n"
                + "2\t465\t2027\tSL\tM\t0.00\t732\t167\t0\t1\t2\t6\t9\n");
            Add(zip, "Fiscategory.txt", "Recid\tSectorcode\tCatcode\tDescription\tDisplayorder\tInuse\tPublished\tRacelevel\tCalautoload\tLastupdate\n"
                + "1\tAL\tTEST\tSynthetic category\t1\t1\t1\t3\t1\t2026-09-22\n");
        }
        if (wrongList) { Assert.Throws<DomainValidationException>(() => FisPointsListReader.Read(output.ToArray())); return; }
        var list = FisPointsListReader.Read(output.ToArray());
        var profile = list.PenaltyRules!.Resolve("TEST", Discipline.Slalom, Gender.Female);
        Assert.Equal(731, profile.FValue); Assert.Equal(166m, profile.MaximumPoints);
        Assert.Equal(29m, profile.Minimum); Assert.Equal(888m, profile.Maximum); Assert.Equal(7m, profile.Adder);
        Assert.Equal(6m, list.PenaltyRules.Resolve("TEST", Discipline.Slalom, Gender.Male).Adder);
        Assert.Throws<DomainValidationException>(() => list.PenaltyRules.Resolve("UNKNOWN", Discipline.Slalom, Gender.Female));
        var snapshot = new PointsListSource(list.ListCode, list.ValidFrom, list.ValidTo, list.PenaltyRules);
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<PointsListSource>(System.Text.Json.JsonSerializer.Serialize(snapshot));
        Assert.Equal(profile, roundTrip!.PenaltyRules!.Resolve("TEST", Discipline.Slalom, Gender.Female));
    }
}
