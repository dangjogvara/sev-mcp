using System.ComponentModel;
using ModelContextProtocol.Server;
using Sev.Client;
using Sev.Client.Models;

namespace Sev.McpServer.Tools;

[McpServerToolType]
public class MeterTools(SevDataService sev, MeterNicknames nicknames)
{
    [McpServerTool(Name = "list_meters", Title = "List electricity meters", ReadOnly = true, Idempotent = true)]
    [Description(
        "Lists the SEV electricity meters on the user's account, with address and install/removal dates. " +
        "get_usage, get_cost and get_co2 accept these meters by ID, nickname, meter name or address, or use all active meters when none are given. " +
        "The list is cached for a few hours, so calling it again is cheap.")]
    public Task<IReadOnlyList<MeterSummary>> ListMetersAsync(CancellationToken cancellationToken) =>
        SevToolErrors.CallAsync(async () =>
            SevToolErrors.ThrowIfEmpty(nicknames.Apply(Summarize(await sev.GetAvailableMetersAsync(cancellationToken)))));

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

    // "Street 1, 2nd floor, 100 Tórshavn", leaving out whatever SEV didn't send.
    private static string FormatAddress(Installation i)
    {
        var place = string.Join(" ", new[] { i.PostalCode, i.PostalDistrict }.Where(HasText).Select(s => s!.Trim()));
        return string.Join(", ", new[] { i.Address, i.Floor, place }.Where(HasText).Select(s => s!.Trim()));
    }

    private static bool HasText(string? s) => !string.IsNullOrWhiteSpace(s);
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
