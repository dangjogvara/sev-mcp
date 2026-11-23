using System.Globalization;

namespace Sev.Core;

// Nicknames for meters from the server's settings (Sev:MeterNicknames:<meter ID>, hosted
// Sev__MeterNicknames__<meter ID>), for when SEV's own portal can't set one: SEV's API only reads
// meter_nickname. A configured nickname replaces SEV's for that meter, in list_meters and wherever meters are
// picked by nickname (get_usage, get_cost, get_co2). A singleton, built once from SevOptions.
public class MeterNicknames
{
    private readonly IReadOnlyDictionary<int, string> _byMeterId;

    public MeterNicknames(IReadOnlyDictionary<int, string> byMeterId) => _byMeterId = byMeterId;

    public static MeterNicknames None { get; } = new(new Dictionary<int, string>());

    // From the configuration section, whose keys are strings. Problems(configured) must be empty.
    public static MeterNicknames From(IReadOnlyDictionary<string, string>? configured) =>
        new((configured ?? new Dictionary<string, string>()).ToDictionary(
            pair => int.Parse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture),
            pair => pair.Value.Trim()));

    // What's wrong with the configured nicknames, in words; empty when they can be used. Two keys for the same
    // meter ("0123" and "123") are a problem too, since it would be unclear which nickname applies.
    public static IReadOnlyList<string> Problems(IReadOnlyDictionary<string, string>? configured)
    {
        var pairs = configured ?? new Dictionary<string, string>();
        var perPair = pairs
            .SelectMany(pair => new[]
            {
                int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                    ? null
                    : $"Sev:MeterNicknames:{pair.Key} isn't a meter ID (a number, as list_meters shows it).",
                string.IsNullOrWhiteSpace(pair.Value) ? $"Sev:MeterNicknames:{pair.Key} is empty." : null,
            });
        var sameMeter = pairs.Keys
            .Select(key => (Key: key, Parsed: int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id), Id: id))
            .Where(k => k.Parsed)
            .GroupBy(k => k.Id)
            .Where(g => g.Count() > 1)
            .Select(g => $"Sev:MeterNicknames has more than one key for meter {g.Key}: {string.Join(", ", g.Select(k => k.Key).Order(StringComparer.Ordinal))}.");
        return perPair.OfType<string>().Concat(sameMeter).ToList();
    }

    public IReadOnlyList<MeterSummary> Apply(IReadOnlyList<MeterSummary> meters) =>
        _byMeterId.Count == 0
            ? meters
            : meters.Select(m => _byMeterId.TryGetValue(m.MeterId, out var nickname) ? m with { Nickname = nickname } : m).ToList();
}
