using Sev.Core;

namespace Sev.Core.Tests;

public class MeterNicknamesTests
{
    private static readonly MeterSummary Kitchen = new(1111, "E350", "E-01", "Kjallari", "Testgøta 1, 100 Tórshavn", null, null, false);
    private static readonly MeterSummary Heat = new(2222, "E360 LTE", "E-02", null, "Testgøta 1, 100 Tórshavn", null, null, false);

    [Fact]
    public void Apply_ReplacesOnlyConfiguredMeters()
    {
        var nicknames = MeterNicknames.From(new Dictionary<string, string> { ["2222"] = " Hitapumpa " });

        var meters = nicknames.Apply([Kitchen, Heat]);

        Assert.Equal(["Kjallari", "Hitapumpa"], meters.Select(m => m.Nickname));
    }

    [Fact]
    public void ConfiguredNickname_ReplacesSevs()
    {
        var nicknames = MeterNicknames.From(new Dictionary<string, string> { ["1111"] = "House" });

        Assert.Equal("House", Assert.Single(nicknames.Apply([Kitchen])).Nickname);
    }

    [Fact]
    public void None_LeavesTheListAsItIs()
    {
        IReadOnlyList<MeterSummary> meters = [Kitchen, Heat];

        Assert.Same(meters, MeterNicknames.None.Apply(meters));
    }

    [Fact]
    public void Problems_TwoKeysForTheSameMeter()
    {
        var problems = MeterNicknames.Problems(new Dictionary<string, string> { ["0123"] = "House", ["123"] = "Garage" });

        Assert.Equal(["Sev:MeterNicknames has more than one key for meter 123: 0123, 123."], problems);
    }

    [Theory]
    [InlineData("abc", "House", "isn't a meter ID")]
    [InlineData("-1", "House", "isn't a meter ID")]
    [InlineData("1111", " ", "is empty")]
    public void Problems_NameTheBadSetting(string key, string value, string expected)
    {
        var problem = Assert.Single(MeterNicknames.Problems(new Dictionary<string, string> { [key] = value }));

        Assert.Contains(expected, problem);
    }
}
