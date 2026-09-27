using System.IO.Compression;
using System.Text;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

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
}
