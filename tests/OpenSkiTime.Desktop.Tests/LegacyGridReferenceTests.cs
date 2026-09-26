namespace OpenSkiTime.Desktop.Tests;

public class LegacyGridReferenceTests
{
    [Fact]
    public async Task Manual_grid_add_edit_restore_and_participation_survive_reopening()
    {
        await using var fixture = new LegacyGridFixture();
        await fixture.InitializeAsync();
        var grid = fixture.Grid;
        var row = await fixture.AddRowAsync();
        Assert.Equal("MÜLLER", Assert.Single((await fixture.ReopenAsync()).Competitors).LastName.Value);

        row.ClubName = "Edited Club";
        await grid.OnCellEditCommittedAsync(row, nameof(row.ClubName));
        Assert.Equal("Edited Club", Assert.Single((await fixture.ReopenAsync()).Competitors).ClubName);
        await grid.RestoreEntryCommand.ExecuteAsync(Assert.Single(grid.ChangeLog));
        Assert.Equal("Original Club", Assert.Single((await fixture.ReopenAsync()).Competitors).ClubName);

        var participation = Assert.Single(row.Participations);
        participation.IsParticipating = true;
        await grid.SaveParticipationAsync(row, participation);
        Assert.True(Assert.Single(Assert.Single((await fixture.ReopenAsync()).Competitors).Participations).IsParticipating);
        participation.IsParticipating = false;
        await grid.SaveParticipationAsync(row, participation);
        Assert.False(Assert.Single(Assert.Single((await fixture.ReopenAsync()).Competitors).Participations).IsParticipating);
        Assert.Empty(fixture.Dialogs.Errors);
    }

    [Fact]
    public async Task Scalar_paste_preview_is_applied_then_selected_row_is_copied()
    {
        await using var fixture = new LegacyGridFixture();
        await fixture.InitializeAsync();
        await fixture.AddRowAsync();
        var grid = fixture.Grid;
        fixture.Clipboard.Text = "Last Name\tFirst Name\tYear\tNation\tClub\nMüller\tHannes\t2007\tGER\tNew Club";
        await grid.PasteCommand.ExecuteAsync(null);
        Assert.True(grid.HasActivePasteSession);
        Assert.Equal("GER", Assert.Single(grid.Competitors).NationCode);
        Assert.Equal("FIN", Assert.Single((await fixture.ReopenAsync()).Competitors).NationCode);

        await grid.ApplyImportCommand.ExecuteAsync(null);
        Assert.False(grid.HasActivePasteSession);
        var persisted = Assert.Single((await fixture.ReopenAsync()).Competitors);
        Assert.Equal("GER", persisted.NationCode);
        Assert.Equal("New Club", persisted.ClubName);
        grid.SelectedItems.Add(Assert.Single(grid.Competitors));
        await grid.CopySelectedRowsCommand.ExecuteAsync(null);
        var lines = fixture.Clipboard.Text!.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("FIS Code\tLast Name\tFirst Name\tYear\tGender\tNation\tClub\t3.1 SL", lines[0]);
        Assert.Equal("123456\tMÜLLER\tHannes\t2007\t\tGER\tNew Club\t", lines[1]);
        Assert.Empty(fixture.Dialogs.Errors);
    }

    [Fact]
    public async Task Delete_can_be_restored_before_commit_and_commit_removes_the_competitor()
    {
        await using var fixture = new LegacyGridFixture();
        await fixture.InitializeAsync();
        var row = await fixture.AddRowAsync();
        var grid = fixture.Grid;
        grid.DeleteCompetitorCommand.Execute(row);
        Assert.Single((await fixture.ReopenAsync()).Competitors);
        await grid.RestoreEntryCommand.ExecuteAsync(Assert.Single(grid.ChangeLog));
        await grid.CommitChangeLogCommand.ExecuteAsync(null);
        Assert.Single((await fixture.ReopenAsync()).Competitors);

        grid.DeleteCompetitorCommand.Execute(row);
        await grid.CommitChangeLogCommand.ExecuteAsync(null);
        Assert.Empty((await fixture.ReopenAsync()).Competitors);
        Assert.Empty(fixture.Dialogs.Errors);
    }
}
