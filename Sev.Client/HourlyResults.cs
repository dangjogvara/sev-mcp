using Sev.Client.Models;

namespace Sev.Client;

// What UsageResult, CostResult and Co2Result have in common, so the MCP tools can handle both the same way.
// Meters holds the readings within the requested range: SEV's answer plus cached days. NotFetched is the
// oldest part of the range that wasn't fetched in this call (SEV's per-call limits or an empty answer);
// null when everything was fetched. EmptyAnswer is true when SEV answered the first request with an empty
// list, which usually means throttling; Meters then holds only cached days, if any.
public interface IHourlyResult
{
    IReadOnlyList<IMeterReadings> Meters { get; }
    (DateTime From, DateTime To)? NotFetched { get; }
    bool EmptyAnswer { get; }
}

// Hourly kWh usage per meter (see IHourlyResult).
public record UsageResult(
    IReadOnlyList<MeterUsage> Meters, (DateTime From, DateTime To)? NotFetched, bool EmptyAnswer = false) : IHourlyResult
{
    IReadOnlyList<IMeterReadings> IHourlyResult.Meters => Meters;
}

// SEV's estimated hourly cost in DKK per meter (see IHourlyResult).
public record CostResult(
    IReadOnlyList<MeterCost> Meters, (DateTime From, DateTime To)? NotFetched, bool EmptyAnswer = false) : IHourlyResult
{
    IReadOnlyList<IMeterReadings> IHourlyResult.Meters => Meters;
}

// SEV's estimated hourly CO2 emission in kg per meter (see IHourlyResult).
public record Co2Result(
    IReadOnlyList<MeterCo2> Meters, (DateTime From, DateTime To)? NotFetched, bool EmptyAnswer = false) : IHourlyResult
{
    IReadOnlyList<IMeterReadings> IHourlyResult.Meters => Meters;
}
