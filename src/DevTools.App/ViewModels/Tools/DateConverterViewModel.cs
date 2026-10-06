using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevTools.App.Services;
using DevTools.Core.Time;

namespace DevTools.App.ViewModels.Tools;

/// <summary>Date &amp; Unix Time converter.</summary>
public sealed partial class DateConverterViewModel : TextToolViewModelBase
{
    private static readonly EpochUnit[] UnitList =
        [EpochUnit.Auto, EpochUnit.Seconds, EpochUnit.Milliseconds, EpochUnit.Microseconds, EpochUnit.Nanoseconds];

    private readonly IReadOnlyList<TimeZoneInfo> _zones;
    private CancellationTokenSource? _live;

    public DateConverterViewModel(ToolServices services)
        : base(services)
    {
        _zones = [TimeZoneInfo.Local, TimeZoneInfo.Utc, .. TimeZoneInfo.GetSystemTimeZones()
            .Where(static z => z.Id != TimeZoneInfo.Local.Id && z.Id != TimeZoneInfo.Utc.Id)];

        ZoneNames = [.. _zones.Select(static (z, i) => i switch
        {
            0 => $"Local — {z.DisplayName}",
            1 => "UTC",
            _ => z.DisplayName,
        })];

        SummaryText = string.Empty;
    }

    public override string ToolId => "date-converter";

    protected override string SuggestedFileName => "date.txt";

    public IReadOnlyList<string> UnitNames { get; } = [.. UnitList.Select(DateTimeConverter.Describe)];

    public IReadOnlyList<string> ZoneNames { get; }

    private static readonly DateOrder[] OrderList = [DateOrder.Auto, DateOrder.DayMonthYear, DateOrder.MonthDayYear, DateOrder.YearMonthDay];

    /// <summary>The date-order choices; Auto names the order the Windows region uses.</summary>
    public IReadOnlyList<string> DateOrderNames { get; } =
    [
        $"Auto ({Pattern(DateTimeConverter.RegionOrder(CultureInfo.CurrentCulture))})",
        "DD-MM-YYYY",
        "MM-DD-YYYY",
        "YYYY-MM-DD",
    ];

    private static string Pattern(DateOrder order) => order switch
    {
        DateOrder.MonthDayYear => "MM-DD-YYYY",
        DateOrder.YearMonthDay => "YYYY-MM-DD",
        _ => "DD-MM-YYYY",
    };

    /// <summary>How a date written all in numbers is read. Index into the list above.</summary>
    [ObservableProperty]
    public partial int DateOrderIndex { get; set; }

    partial void OnDateOrderIndexChanged(int value) => OnOptionChanged();

    private DateOrder Order => OrderList[Math.Clamp(DateOrderIndex, 0, OrderList.Length - 1)];

    [ObservableProperty]
    public partial int UnitIndex { get; set; }

    [ObservableProperty]
    public partial int ZoneIndex { get; set; }

    /// <summary>Keeps the input on the current time, ticking once a second.</summary>
    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial string SummaryText { get; set; }

    public ObservableCollection<DateField> Fields { get; } = [];

    private EpochUnit Unit => UnitList[Math.Clamp(UnitIndex, 0, UnitList.Length - 1)];

    private TimeZoneInfo Zone => _zones[Math.Clamp(ZoneIndex, 0, _zones.Count - 1)];

    partial void OnUnitIndexChanged(int value) => OnOptionChanged();

    partial void OnZoneIndexChanged(int value) => OnOptionChanged();

    partial void OnIsLiveChanged(bool value)
    {
        _live?.Cancel();
        _live = null;

        if (value)
        {
            _live = new CancellationTokenSource();
            _ = TickAsync(_live.Token);
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            do
            {
                UiDispatcher.Run(SetNow);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
            // Live mode switched off, or the page closed.
        }
    }

    /// <summary>Fills the input with the current time as a timestamp in the chosen unit.</summary>
    [RelayCommand]
    private void Now() => SetNow();

    private void SetNow()
    {
        var now = DateTimeOffset.UtcNow;
        var sinceEpoch = now.Ticks - DateTime.UnixEpoch.Ticks;

        Input = Unit switch
        {
            EpochUnit.Milliseconds => (sinceEpoch / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture),
            EpochUnit.Microseconds => (sinceEpoch / 10).ToString(CultureInfo.InvariantCulture),
            EpochUnit.Nanoseconds => (sinceEpoch * 100).ToString(CultureInfo.InvariantCulture),
            _ => (sinceEpoch / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture),
        };
    }

    [RelayCommand]
    private void CopyValue(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            Services.Clipboard.SetText(value);
            SetSuccess("Copied to clipboard.");
        }
    }

    protected override Task RunCoreAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(Input))
        {
            Fields.Clear();
            Output = string.Empty;
            SummaryText = "Enter a Unix timestamp such as 1700000000, or a date such as 2024-05-01T12:00:00Z — or press Now.";
            ClearMessage();
            return Task.CompletedTask;
        }

        // Cheap enough to run on the UI thread, and the live clock runs it every second.
        var parsed = DateTimeConverter.Parse(Input, Unit, Zone, Order, CultureInfo.CurrentCulture);

        if (!parsed.IsSuccess)
        {
            SetError(parsed.Error!);
            return Task.CompletedTask;
        }

        var value = parsed.Value!;
        var fields = DateTimeConverter.Fields(value.Instant, Zone, DateTimeOffset.UtcNow);

        Fields.Clear();
        var text = new StringBuilder();
        foreach (var field in fields)
        {
            Fields.Add(field);
            text.Append(field.Label).Append(": ").Append(field.Value).Append('\n');
        }

        Output = text.ToString();
        SummaryText = value.WasTimestamp
            ? $"Read as a Unix timestamp in {DateTimeConverter.Describe(value.Unit).ToLowerInvariant()}" + (Unit == EpochUnit.Auto ? " (detected from its size)." : ".")
            : $"Read as {value.ReadAs ?? "a date"}" + (Input.TrimEnd().EndsWith('Z') ? ", in UTC." : $", in {ZoneNames[ZoneIndex].Split(" — ")[0]}.");

        if (parsed.HasWarning)
        {
            SetWarning(parsed.Warning!);
        }
        else if (!IsLive)
        {
            ClearMessage();
        }

        return Task.CompletedTask;
    }

    public override void Deactivate()
    {
        IsLive = false;
        base.Deactivate();
    }

    public override void Clear()
    {
        IsLive = false;
        base.Clear();
        Fields.Clear();
    }

    protected override void CaptureState(ToolState state)
    {
        base.CaptureState(state);
        state.Set("unit", UnitIndex);
        state.Set("order", DateOrderIndex);
        state.Set("zone", Zone.Id);
    }

    protected override void RestoreState(ToolState state)
    {
        base.RestoreState(state);
        UnitIndex = Math.Clamp(state.GetInt("unit"), 0, UnitList.Length - 1);
        DateOrderIndex = Math.Clamp(state.GetInt("order"), 0, OrderList.Length - 1);

        var zoneId = state.GetString("zone");
        var index = _zones.ToList().FindIndex(z => z.Id == zoneId);
        ZoneIndex = index < 0 ? 0 : index;
    }

    protected override void ResetOptions()
    {
        IsLive = false;
        UnitIndex = 0;
        DateOrderIndex = 0;
        ZoneIndex = 0;
    }

    protected override async Task OnActivatedAsync()
    {
        await base.OnActivatedAsync();

        // An empty converter is more useful showing the time it is now.
        if (string.IsNullOrWhiteSpace(Input))
        {
            SetNow();
        }
    }
}
