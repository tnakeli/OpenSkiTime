namespace OpenSkiTime.LiveTiming;

public enum ControlPanelAction { State, Activate, Start, Stop, Refresh, Delete, DeleteAll, Reset }
public sealed record LiveConnectionSettings(string LocalEndpoint, string CloudEndpoint, string FisHttpsEndpoint,
    string FisTcpHost, int FisTcpPort, string FisPassword, bool FisUseTcp, string TimeZone);
public sealed record ControlPanelInput(Guid RequestId, ControlPanelAction Action, Guid? CompetitionId = null,
    LiveSnapshot? Snapshot = null, PublisherKind? Channel = null, LiveConnectionSettings? Settings = null, string? PreparationError = null);
public sealed record LiveChannelHealth(PublisherKind Kind, PublisherHealth Health, int? ProcessId);
public sealed record ControlPanelOutput(Guid RequestId, LiveChannelHealth[] Channels, string Error = "",
    Guid? SnapshotRequestId = null, LiveConnectionSettings? RequestedSettings = null);
