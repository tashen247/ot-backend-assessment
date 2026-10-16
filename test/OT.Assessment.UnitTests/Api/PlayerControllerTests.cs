using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Core.Messaging;
using OT.Assessment.Core.Models;

namespace OT.Assessment.UnitTests.Api;

public class PlayerControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public PlayerControllerTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Publisher.ClearReceivedCalls();
        _factory.Publisher.PublishAsync(default, default).ReturnsForAnyArgs(Task.CompletedTask);
        _client = factory.CreateClient();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Post_accepts_the_brief_sample_on_the_route_the_tester_uses_and_returns_200()
    {
        // The Tester posts to /api/Player/CasinoWager and only counts HTTP 200 as success.
        var response = await _client.PostAsync("/api/Player/CasinoWager", Json(TestData.BriefSampleJson));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _factory.Publisher.Received(1).PublishAsync(
            Arg.Is<CasinoWagerMessage>(m =>
                m.WagerId == Guid.Parse("aa6700eb-1a06-483e-9739-d293dc7a9383") &&
                m.Username == "Jay.Bernhard67" &&       // "Username" casing in the payload
                m.Duration == 1827254 &&                // "Duration" casing in the Tester
                m.Amount == 38273.974454660885m &&
                m.CreatedDateTime == DateTimeOffset.Parse("2024-05-04T02:25:05.9906387+02:00")),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("accountId", "\"not-a-guid\"")]
    [InlineData("amount", "-5")]
    [InlineData("countryCode", "\"ZAF\"")]
    [InlineData("numberOfBets", "0")]
    [InlineData("wagerId", "\"00000000-0000-0000-0000-000000000000\"")]
    [InlineData("accountId", "\"00000000-0000-0000-0000-000000000000\"")]
    public async Task Post_rejects_invalid_wagers_with_400_and_publishes_nothing(string field, string value)
    {
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(TestData.BriefSampleJson)!;
        body[field] = JsonDocument.Parse(value).RootElement;

        var response = await _client.PostAsJsonAsync("/api/player/casinowager", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _factory.Publisher.DidNotReceiveWithAnyArgs().PublishAsync(default, default);
    }

    [Fact]
    public async Task Post_rejects_a_wager_with_missing_ids()
    {
        var response = await _client.PostAsync("/api/player/casinowager", Json("""{ "provider": "p", "gameName": "g" }"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_returns_503_when_the_message_bus_is_unavailable()
    {
        _factory.Publisher.PublishAsync(default, default)
            .ThrowsAsyncForAnyArgs(new MessagePublishException("down", new Exception()));

        var response = await _client.PostAsync("/api/player/casinowager", Json(TestData.BriefSampleJson));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Post_tells_the_caller_how_long_to_wait_while_the_publish_circuit_is_open()
    {
        _factory.Publisher.PublishAsync(default, default)
            .ThrowsAsyncForAnyArgs(new MessagePublishException("circuit open", new Exception(), TimeSpan.FromSeconds(12.3)));

        var response = await _client.PostAsync("/api/player/casinowager", Json(TestData.BriefSampleJson));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(13), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Get_casino_returns_the_paged_shape_from_the_brief()
    {
        var accountId = Guid.NewGuid();
        _factory.Players.GetCasinoWagersAsync(accountId, 2, 5, Arg.Any<CancellationToken>()).Returns(
            new PagedResult<PlayerWager>(
                [new PlayerWager { WagerId = Guid.NewGuid(), Game = "Game", Provider = "Provider", Amount = 500m, CreatedDate = DateTimeOffset.UtcNow }],
                page: 2, pageSize: 5, total: 6));

        using var json = JsonDocument.Parse(await _client.GetStringAsync($"/api/player/{accountId}/casino?pageSize=5&page=2"));
        var root = json.RootElement;

        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.Equal(5, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(6, root.GetProperty("total").GetInt32());
        Assert.Equal(2, root.GetProperty("totalPages").GetInt32());
        var wager = root.GetProperty("data")[0];
        foreach (var property in new[] { "wagerId", "game", "provider", "amount", "createdDate" })
        {
            Assert.True(wager.TryGetProperty(property, out _), $"missing {property}");
        }
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=0")]
    public async Task Get_casino_rejects_out_of_range_paging(string query)
    {
        var response = await _client.GetAsync($"/api/player/{Guid.NewGuid()}/casino?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_top_spenders_returns_the_shape_from_the_brief()
    {
        _factory.Players.GetTopSpendersAsync(3, Arg.Any<CancellationToken>()).Returns(
            [new TopSpender { AccountId = Guid.NewGuid(), Username = "testUser1", TotalAmountSpend = 150m }]);

        using var json = JsonDocument.Parse(await _client.GetStringAsync("/api/player/topSpenders?count=3"));
        var first = json.RootElement[0];

        Assert.Equal("testUser1", first.GetProperty("username").GetString());
        Assert.Equal(150m, first.GetProperty("totalAmountSpend").GetDecimal());
        Assert.True(first.TryGetProperty("accountId", out _));
    }

    [Fact]
    public async Task Get_top_spenders_rejects_a_count_out_of_range()
    {
        var response = await _client.GetAsync("/api/player/topSpenders?count=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Liveness_endpoint_responds()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
