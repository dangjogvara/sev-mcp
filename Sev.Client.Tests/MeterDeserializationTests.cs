using Sev.Client.Models;

namespace Sev.Client.Tests;

// Cases the OpenAPI spec allows but real data hasn't shown yet: meter dates are free-form
// nullable strings and meter_nickname is typed as "object".
public class MeterDeserializationTests
{
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly SevClient _client;

    public MeterDeserializationTests()
    {
        _client = new SevClient(new HttpClient(_handler) { BaseAddress = SevClient.DefaultBaseAddress });
    }

    [Theory]
    [InlineData("\"2013-03-12\"", 2013, 3, 12)]
    [InlineData("\"2013-03-12T00:00:00\"", 2013, 3, 12)]
    [InlineData("\"2023-10-17T23:41:38.715Z\"", 2023, 10, 17)]
    // An explicit offset keeps the written date: converted to almost any machine's time zone, 01:00 at +14:00
    // would be the day before.
    [InlineData("\"2023-10-17T01:00:00+14:00\"", 2023, 10, 17)]
    [InlineData("\"2023-10-17T23:41:38-08:00\"", 2023, 10, 17)]
    public async Task Dates_AcceptDateAndDateTimeStrings(string json, int year, int month, int day)
    {
        var meter = await DeserializeMeterAsync(installDate: json, removalDate: json);

        Assert.Equal(new DateOnly(year, month, day), meter.InstallDate);
        Assert.Equal(new DateOnly(year, month, day), meter.RemovalDate);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"not a date\"")]
    [InlineData("{}")]
    public async Task Dates_UnparseableBecomeNull(string json)
    {
        var meter = await DeserializeMeterAsync(installDate: json, removalDate: json);

        Assert.Null(meter.InstallDate);
        Assert.Null(meter.RemovalDate);
    }

    [Fact]
    public async Task Nickname_StringIsKept()
    {
        var meter = await DeserializeMeterAsync(nickname: "\"Kjallari\"");

        Assert.Equal("Kjallari", meter.MeterNickname);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"name\":\"Kjallari\"}")]
    [InlineData("[]")]
    [InlineData("42")]
    public async Task Nickname_NonStringBecomesNull(string json)
    {
        var meter = await DeserializeMeterAsync(nickname: json);

        Assert.Null(meter.MeterNickname);
    }

    [Fact]
    public async Task OddMeter_DoesNotAffectOtherMeters()
    {
        _handler.Respond(WrapMeters(
            MeterJson(1111, "\"2013-03-12\"", "null", "null"),
            MeterJson(2222, "\"garbage\"", "{}", "{}")));

        var meters = (await _client.GetAvailableMetersAsync()).Single().Installations.Single().Meters;

        Assert.Equal([1111, 2222], meters.Select(m => m.MeterId));
        Assert.Equal(new DateOnly(2013, 3, 12), meters[0].InstallDate);
        Assert.Null(meters[1].InstallDate);
    }

    private async Task<Meter> DeserializeMeterAsync(string installDate = "\"2013-03-12\"", string removalDate = "null", string nickname = "null")
    {
        _handler.Respond(WrapMeters(MeterJson(1111, installDate, removalDate, nickname)));

        var customers = await _client.GetAvailableMetersAsync();
        return customers.Single().Installations.Single().Meters.Single();
    }

    private static string MeterJson(int meterId, string installDate, string removalDate, string nickname) => $$"""
        {
          "meter_id": {{meterId}},
          "meter_nickname": {{nickname}},
          "serial_number": "11111111",
          "meter_name": "E350",
          "meter_type": "E-01",
          "install_date": {{installDate}},
          "removal_date": {{removalDate}}
        }
        """;

    private static string WrapMeters(params string[] meters) => $$"""
        [
          {
            "customer_name": "Test Customer",
            "installations": [
              {
                "move_in_date": "2019-06-01T00:00:00",
                "move_on_date": null,
                "inst_id": 10001,
                "inst_nickname": null,
                "address": "Testgøta 1",
                "floor": null,
                "postal_code": "100",
                "postal_district": "Tórshavn",
                "tarif_number": 310,
                "billing_type": "OS",
                "meters": [{{string.Join(",", meters)}}]
              }
            ]
          }
        ]
        """;
}
