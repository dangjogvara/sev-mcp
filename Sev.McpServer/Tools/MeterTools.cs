using System.ComponentModel;
using ModelContextProtocol.Server;
using Sev.Client;
using Sev.Core;

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
            SevToolErrors.ThrowIfEmpty(nicknames.Apply(MeterSummary.FromCustomers(await sev.GetAvailableMetersAsync(cancellationToken)))),
            cancellationToken);
}
