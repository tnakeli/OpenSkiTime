using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace OpenSkiTime.Desktop;

// Publish a coherent ranking snapshot: filtered/sorted collection views cannot safely
// interpret a sequence of intermediate replacements while competitors change ranks.
public sealed class TimingRankingRows : ObservableCollection<TimingGridRow>
{
    private bool _updating;
    private bool _changed;

    public void UpdateRows(Action update)
    {
        ArgumentNullException.ThrowIfNull(update);
        _updating = true; _changed = false;
        try { update(); }
        finally
        {
            _updating = false;
            if (_changed) { base.OnCollectionChanged(new(NotifyCollectionChangedAction.Reset)); }
        }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_updating) { _changed = true; }
        else { base.OnCollectionChanged(e); }
    }
}
