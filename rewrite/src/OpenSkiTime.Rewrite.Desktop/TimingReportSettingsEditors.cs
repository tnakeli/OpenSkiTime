using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.Rewrite.Application;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class TimingReportPersonEditor : ObservableObject
{
    [ObservableProperty] private string _firstName = "";
    [ObservableProperty] private string _lastName = "";
    [ObservableProperty] private string _nation = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _phone = "";
    [ObservableProperty] private string _number = "";
    [ObservableProperty] private string _company = "";
    public TimingReportPerson Values => new(FirstName.Trim(), LastName.Trim().ToUpperInvariant(), Nation.Trim().ToUpperInvariant(),
        Email.Trim(), Phone.Trim(), Number.Trim(), Company.Trim());
    public void Load(TimingReportPerson p)
    { ArgumentNullException.ThrowIfNull(p); FirstName = p.FirstName; LastName = p.LastName; Nation = p.Nation; Email = p.Email; Phone = p.Phone; Number = p.Number; Company = p.Company; }
}

public sealed partial class TimingReportDeviceEditor(string label, int category) : ObservableObject
{
    private int? _validUntilSeason;
    private DateTimeOffset? _homologationRetrievedAt;
    private bool _loading;
    public string Label { get; } = label;
    public int Category { get; } = category;
    [ObservableProperty] private string _brand = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _serial = "";
    [ObservableProperty] private string _homologation = "";
    public string Validity => _validUntilSeason?.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ?? (_homologationRetrievedAt is null ? "Manual / unverified" : "No expiry supplied");
    public TimingReportDevice Values => new(Brand.Trim(), Model.Trim(), Serial.Trim(), Homologation.Trim())
        { ValidUntilSeason = _validUntilSeason, HomologationRetrievedAt = _homologationRetrievedAt };
    public void Load(TimingReportDevice d)
    {
        ArgumentNullException.ThrowIfNull(d);
        _loading = true;
        try { Brand = d.Brand; Model = d.Model; Serial = d.Serial; Homologation = d.Homologation;
            _validUntilSeason = d.ValidUntilSeason; _homologationRetrievedAt = d.HomologationRetrievedAt; }
        finally { _loading = false; }
        OnPropertyChanged(nameof(Validity));
    }
    partial void OnBrandChanged(string value) => ClearHomologationEvidence();
    partial void OnModelChanged(string value) => ClearHomologationEvidence();
    partial void OnHomologationChanged(string value) => ClearHomologationEvidence();
    private void ClearHomologationEvidence()
    {
        if (_loading) { return; }
        _validUntilSeason = null; _homologationRetrievedAt = null; OnPropertyChanged(nameof(Validity));
    }
}
