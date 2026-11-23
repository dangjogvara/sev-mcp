using Sev.Client.Models;

namespace Sev.Core;

// One meter, flattened from SEV's customer → installation → meter hierarchy: what list_meters shows and what the
// usage tools pick meters from.
public record MeterSummary(
    int MeterId,
    string MeterName,
    string MeterType,
    string? Nickname,
    string Address,
    DateOnly? InstallDate,
    DateOnly? RemovalDate,
    bool Removed
)
{
    // One row per meter, in SEV's order.
    public static IReadOnlyList<MeterSummary> FromCustomers(IReadOnlyList<Customer> customers) =>
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
