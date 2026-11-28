using Microsoft.Extensions.Logging;
using SuperBar.Core.Runtime;
using SuperBar.Core.Shell;
using Windows.System.Power;
using AggregateBattery = Windows.Devices.Power.Battery;

namespace SuperBar.Windows.SystemStatus;

/// <summary>
/// <see cref="IBatteryStateService"/> over <see cref="PowerManager"/> (presence, percentage, the discharge estimate
/// Windows shows, AC power, battery saver) and <see cref="AggregateBattery.AggregateBattery"/> (charge rate and
/// capacities, for the time until fully charged). Every one of their change events triggers a re-read, so plugging in,
/// unplugging, a charging state change or a one-percent drop shows within a moment; nothing polls.
/// <para>
/// A machine without a system battery reports <see cref="BatteryStatus.NotPresent"/>: the state is then
/// <see cref="BatteryState.NotPresent"/> and the tray hides the indicator.
/// </para>
/// </summary>
internal sealed class WinRtBatteryStateService(IUiDispatcher dispatcher, ILogger<WinRtBatteryStateService> logger)
    : WatchedState<BatteryState>(dispatcher, logger, BatteryState.NotPresent, TimeSpan.FromMilliseconds(100)), IBatteryStateService
{
    /// <summary>Estimates beyond this are Windows' "unknown" sentinels (it reports huge spans while it has none).</summary>
    private static readonly TimeSpan MaxEstimate = TimeSpan.FromDays(2);

    private AggregateBattery? _aggregate;

    protected override void Attach()
    {
        PowerManager.BatteryStatusChanged += OnPowerChanged;
        PowerManager.PowerSupplyStatusChanged += OnPowerChanged;
        PowerManager.RemainingChargePercentChanged += OnPowerChanged;
        PowerManager.RemainingDischargeTimeChanged += OnPowerChanged;
        PowerManager.EnergySaverStatusChanged += OnPowerChanged;

        try
        {
            _aggregate = AggregateBattery.AggregateBattery;
            _aggregate.ReportUpdated += OnReportUpdated;
        }
        catch (Exception ex)
        {
            // The charge-rate estimate is a nicety: without it the tooltip just has no "until fully charged" line.
            Logger.LogDebug(ex, "Aggregate battery unavailable");
            _aggregate = null;
        }

        // Read at once (it is cheap), so the icon is right on the bar's first frame instead of popping in.
        PublishNow(Read());
    }

    protected override void Detach()
    {
        PowerManager.BatteryStatusChanged -= OnPowerChanged;
        PowerManager.PowerSupplyStatusChanged -= OnPowerChanged;
        PowerManager.RemainingChargePercentChanged -= OnPowerChanged;
        PowerManager.RemainingDischargeTimeChanged -= OnPowerChanged;
        PowerManager.EnergySaverStatusChanged -= OnPowerChanged;
        if (_aggregate is not null)
        {
            _aggregate.ReportUpdated -= OnReportUpdated;
            _aggregate = null;
        }
    }

    private void OnPowerChanged(object? sender, object e) => RequestRead();

    private void OnReportUpdated(AggregateBattery sender, object args) => RequestRead();

    protected override BatteryState Read()
    {
        var status = PowerManager.BatteryStatus;
        if (status == BatteryStatus.NotPresent)
        {
            return BatteryState.NotPresent;
        }

        var percent = Math.Clamp(PowerManager.RemainingChargePercent, 0, 100);
        var pluggedIn = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
        var charging = status == BatteryStatus.Charging;
        var saver = PowerManager.EnergySaverStatus == EnergySaverStatus.On;

        TimeSpan? remaining = null;
        if (!pluggedIn)
        {
            var discharge = PowerManager.RemainingDischargeTime;
            if (discharge > TimeSpan.Zero && discharge < MaxEstimate)
            {
                remaining = discharge;
            }
        }

        return new BatteryState(true, percent, charging, pluggedIn, saver, remaining, charging ? TimeToFull() : null);
    }

    /// <summary>(Full − remaining capacity) ÷ charge rate, when the battery reports all three.</summary>
    private TimeSpan? TimeToFull()
    {
        try
        {
            var report = (_aggregate ?? AggregateBattery.AggregateBattery).GetReport();
            if (report.ChargeRateInMilliwatts is { } rate && rate > 0
                && report.FullChargeCapacityInMilliwattHours is { } full
                && report.RemainingCapacityInMilliwattHours is { } left
                && full > left)
            {
                var time = TimeSpan.FromHours((full - left) / (double)rate);
                return time < MaxEstimate ? time : null;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not read the battery report");
        }

        return null;
    }
}
