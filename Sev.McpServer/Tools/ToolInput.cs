using ModelContextProtocol;
using Sev.Core;

namespace Sev.McpServer.Tools;

// Reads the from / to / granularity arguments that get_usage, get_cost, get_co2 and get_heatpump_history share,
// with Sev.Core's Periods. Only an McpException's message reaches Claude, so the core's InvalidInputException is
// passed on as one, and Claude can correct the call.
public static class ToolInput
{
    public static (Granularity Granularity, DateTime From, DateTime To) ParsePeriod(string from, string to, string granularity)
    {
        try
        {
            var bucket = Periods.ParseGranularity(granularity);
            var (fromTime, toTime) = Periods.ParseRange(from, to, bucket);
            return (bucket, fromTime, toTime);
        }
        catch (InvalidInputException ex)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
