using V6Meter.Core.Aggregation;
using V6Meter.Core.Config;
using V6Meter.Core.Etw;
using V6Meter.Core.Model;

namespace V6Meter.Core.Billing;

public static class BillingRules
{
    /// <summary>Billed bytes for a set of counters: download, plus upload if billing_counts_upload.</summary>
    public static long BilledBytes(TrafficCounters t, AppConfig cfg)
    {
        long b = BilledDown(t, cfg);
        if (cfg.BillingCountsUpload)
        {
            b += t[TrafficCategory.V4Billed, NetDirection.Send];
            if (cfg.Nat64IsBilled)
                b += t[TrafficCategory.V6Nat64, NetDirection.Send];
        }
        return b;
    }

    /// <summary>Billed download only; drives the rate alert and the red tray icon.</summary>
    public static long BilledDown(TrafficCounters t, AppConfig cfg) =>
        t[TrafficCategory.V4Billed, NetDirection.Recv] + (cfg.Nat64IsBilled ? t[TrafficCategory.V6Nat64, NetDirection.Recv] : 0);
}
