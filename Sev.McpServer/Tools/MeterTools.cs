using System.ComponentModel;
using ModelContextProtocol.Server;
using Sev.Client;
using Sev.Client.Models;

namespace Sev.McpServer.Tools;

[McpServerToolType]
public class MeterTools(SevDataService sev)
{
    [McpServerTool(Name = "list_meters", Title = "List electricity meters", ReadOnly = true, Idempotent = true)]
    [Description(
        "Lists the SEV electricity meters on the user's account, with address and install/removal dates. " +
        "Use the meterId values with the usage tools. The list is cached for a few hours, so calling it again is cheap.")]
    public Task<IReadOnlyList<MeterSummary>> ListMetersAsync(CancellationToken cancellationToken) =>
        SevToolErrors.CallAsync(async () =>
            SevToolErrors.ThrowIfEmpty(Summarize(await sev.GetAvailableMetersAsync(cancellationToken))));

    // Flattens SEV's customer → installation → meter hierarchy into one row per meter.
    public static IReadOnlyList<MeterSummary> Summarize(IReadOnlyList<Customer> customers) =>
        customers
            .SelectMany(c => c.Installations)
            .SelectMany(i => i.Meters, (i, m) => new MeterSummary(
                m.MeterId,
                m.MeterName,
                m.MeterType,
                m.MeterNickname,
                FormatAddress(i),
                m.InstallDate,
                m.RemovalDate,
                Removed: m.RemovalDate is not null))
            .ToList();

    private static string FormatAddress(Installation i)
    {
        var street = string.IsNullOrWhiteSpace(i.Floor) ? i.Address.Trim() : $"{i.Address.Trim()}, {i.Floor.Trim()}";
        return $"{street}, {i.PostalCode} {i.PostalDistrict}";
    }
}

public record MeterSummary(
    int MeterId,
    string MeterName,
    string MeterType,
    string? Nickname,
    string Address,
    DateOnly? InstallDate,
    DateOnly? RemovalDate,
    bool Removed
);
