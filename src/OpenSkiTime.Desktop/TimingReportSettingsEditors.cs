using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

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

/// <summary>A catalogue model offered for one equipment row. The editable model box shows <see cref="ToString"/>.</summary>
public sealed record TimingDeviceModelChoice(FisTimingDevice Device)
{
    public string Details => Device.HomologationCode + (Device.ValidUntilSeason is { } season ? $" · through {season}" : "")
        + (Device.Valid ? "" : " · expired");
    public override string ToString() => Device.Name;
}

/// <summary>
/// One report equipment row. With a cached FIS catalogue the row offers the brands of its equipment type and, once a
/// brand is chosen, only that brand's models of the type; choosing a model fills the homologation and its evidence.
/// Brand, model and homologation stay free text so equipment can be entered manually offline.
/// </summary>
public sealed partial class TimingReportDeviceEditor(string label, int category, params int[] alsoCategories) : ObservableObject
{
    private int? _validUntilSeason;
    private DateTimeOffset? _homologationRetrievedAt;
    private bool _loading;
    private FisTimingDevice[] _catalogue = [];
    private DateTimeOffset? _catalogueRetrievedAt;
    public string Label { get; } = label;
    public int Category { get; } = category;
    /// <summary>FIS catalogue categories offered for this row (the start device also accepts start doors).</summary>
    public IReadOnlyList<int> Categories { get; } = [category, .. alsoCategories];
    public IReadOnlyList<string> BrandChoices { get; private set; } = [];
    public IReadOnlyList<TimingDeviceModelChoice> ModelChoices { get; private set; } = [];
    [ObservableProperty] private TimingDeviceModelChoice? _selectedModel;
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
        RefreshModelChoices();
    }
    public void SetCatalogue(IEnumerable<FisTimingDevice> devices, DateTimeOffset? retrievedAt)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _catalogue = devices.Where(x => Categories.Contains(x.CategoryId)).ToArray();
        _catalogueRetrievedAt = retrievedAt;
        BrandChoices = _catalogue.Select(x => x.CompanyName).Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        OnPropertyChanged(nameof(BrandChoices));
        RefreshModelChoices();
    }

    /// <summary>Applies a catalogue model: brand, model, homologation and its evidence. The serial number is kept.</summary>
    public void Apply(FisTimingDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Load(new(device.CompanyName, device.Name, Serial, device.HomologationCode)
            { ValidUntilSeason = device.ValidUntilSeason, HomologationRetrievedAt = _catalogueRetrievedAt });
    }

    // Models of the chosen brand; a brand that names no catalogue company exactly narrows by its text, none lists all.
    private void RefreshModelChoices()
    {
        var brand = Brand.Trim();
        var exact = _catalogue.Where(x => string.Equals(x.CompanyName, brand, StringComparison.OrdinalIgnoreCase)).ToArray();
        var models = exact.Length > 0 || brand.Length == 0 ? (brand.Length == 0 ? _catalogue : exact)
            : _catalogue.Where(x => x.CompanyName.Contains(brand, StringComparison.OrdinalIgnoreCase)).ToArray();
        var choices = models.OrderBy(x => !x.Valid).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(x => x.ValidUntilSeason).Select(x => new TimingDeviceModelChoice(x)).ToArray();
        // An unchanged list keeps its instance, so the model box does not lose its selection or text.
        if (choices.SequenceEqual(ModelChoices)) { return; }
        ModelChoices = choices;
        OnPropertyChanged(nameof(ModelChoices));
    }

    partial void OnSelectedModelChanged(TimingDeviceModelChoice? value)
    {
        if (value is not null && !_loading) { Apply(value.Device); }
    }
    partial void OnBrandChanged(string value) { ClearHomologationEvidence(); if (!_loading) { RefreshModelChoices(); } }
    partial void OnModelChanged(string value) => ClearHomologationEvidence();
    partial void OnHomologationChanged(string value) => ClearHomologationEvidence();
    private void ClearHomologationEvidence()
    {
        if (_loading) { return; }
        _validUntilSeason = null; _homologationRetrievedAt = null; OnPropertyChanged(nameof(Validity));
    }
}
