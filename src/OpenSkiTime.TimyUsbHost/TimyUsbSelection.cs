namespace OpenSkiTime.TimyUsbHost;

// SDK IDs identify connections, not physical devices. Automatic selection may resume
// on a new ID after disconnect; the receiver must start a new clock context for it.
internal sealed class TimyUsbSelection(string requestedId)
{
    public string SelectedId { get; private set; } = "";
    public string WaitingFor => requestedId.Length == 0 ? "the next connected Timy" : "Timy " + requestedId;

    public bool Connect(string id)
    {
        if (SelectedId.Length == 0 && (requestedId.Length == 0 || requestedId == id)) { SelectedId = id; }
        return SelectedId == id;
    }

    public bool Disconnect(string id)
    {
        if (!Accepts(id)) { return false; }
        SelectedId = "";
        return true;
    }

    public bool Accepts(string id) => SelectedId.Length != 0 && SelectedId == id;
}
