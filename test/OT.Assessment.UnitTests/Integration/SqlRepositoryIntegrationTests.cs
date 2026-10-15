using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Infrastructure;

namespace OT.Assessment.UnitTests.Integration;

/// <summary>Exercises the stored procedures against a real database. Each test uses fresh account ids, so tests do not interfere.</summary>
public class SqlRepositoryIntegrationTests
{
    private readonly ICasinoWagerWriter _writer;
    private readonly IPlayerReadRepository _players;

    public SqlRepositoryIntegrationTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                [$"ConnectionStrings:{ServiceCollectionExtensions.ConnectionStringName}"] =
                    Environment.GetEnvironmentVariable(SqlFactAttribute.EnvironmentVariable) ?? "Server=unused"
            })
            .Build();

        var services = new ServiceCollection().AddSqlServerPersistence(configuration).BuildServiceProvider();
        _writer = services.GetRequiredService<ICasinoWagerWriter>();
        _players = services.GetRequiredService<IPlayerReadRepository>();
    }

    [SqlFact]
    public async Task Writing_the_same_wagers_twice_stores_and_totals_them_once()
    {
        var accountId = Guid.NewGuid();
        var wagers = new[] { TestData.Wager(accountId, 10.5m), TestData.Wager(accountId, 20.25m) };

        Assert.Equal(2, await _writer.WriteBatchAsync(wagers, CancellationToken.None));
        Assert.Equal(0, await _writer.WriteBatchAsync(wagers, CancellationToken.None));
        Assert.Equal(0, await _writer.WriteBatchAsync([wagers[0], wagers[0]], CancellationToken.None));

        var page = await _players.GetCasinoWagersAsync(accountId, 1, 10, CancellationToken.None);
        Assert.Equal(2, page.Total);
        Assert.Equal(30.75m, page.Data.Sum(w => w.Amount));
    }

    [SqlFact]
    public async Task Returns_wagers_latest_first_and_pages_them()
    {
        var accountId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var wagers = Enumerable.Range(0, 5).Select(i => TestData.Wager(accountId, i + 1, start.AddMinutes(i))).ToList();
        await _writer.WriteBatchAsync(wagers, CancellationToken.None);

        var first = await _players.GetCasinoWagersAsync(accountId, 1, 2, CancellationToken.None);
        var last = await _players.GetCasinoWagersAsync(accountId, 3, 2, CancellationToken.None);

        Assert.Equal(5, first.Total);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal([wagers[4].WagerId, wagers[3].WagerId], first.Data.Select(w => w.WagerId));
        Assert.Equal([wagers[0].WagerId], last.Data.Select(w => w.WagerId));
        Assert.Equal("Game A", first.Data[0].Game);
        Assert.Equal("Provider A", first.Data[0].Provider);
    }

    [SqlFact]
    public async Task Unknown_player_returns_an_empty_page()
    {
        var page = await _players.GetCasinoWagersAsync(Guid.NewGuid(), 1, 10, CancellationToken.None);

        Assert.Empty(page.Data);
        Assert.Equal(0, page.Total);
    }

    [SqlFact]
    public async Task Top_spenders_are_ordered_by_total_amount_descending()
    {
        // Amounts grow with the clock, so this run's players lead the list even against
        // players left behind by earlier runs or by the Tester.
        var baseAmount = DateTime.UtcNow.Ticks / 10_000m;
        var big = Guid.NewGuid();
        var bigger = Guid.NewGuid();
        await _writer.WriteBatchAsync(
        [
            TestData.Wager(big, baseAmount + 1),
            TestData.Wager(bigger, baseAmount / 2 + 1),
            TestData.Wager(bigger, baseAmount / 2 + 1)
        ], CancellationToken.None);

        var top = await _players.GetTopSpendersAsync(2, CancellationToken.None);

        Assert.Equal([bigger, big], top.Select(t => t.AccountId));
        Assert.Equal(Math.Round(baseAmount + 2, 4), top[0].TotalAmountSpend);
    }

    [SqlFact]
    public async Task Same_game_name_under_two_providers_is_two_games()
    {
        var accountId = Guid.NewGuid();
        var providerA = $"Provider {Guid.NewGuid():N}";
        var providerB = $"Provider {Guid.NewGuid():N}";
        await _writer.WriteBatchAsync(
        [
            TestData.Wager(accountId, provider: providerA, game: "Shared Name"),
            TestData.Wager(accountId, provider: providerB, game: "Shared Name")
        ], CancellationToken.None);

        var page = await _players.GetCasinoWagersAsync(accountId, 1, 10, CancellationToken.None);

        Assert.Equal(new[] { providerA, providerB }.Order(), page.Data.Select(w => w.Provider).Order());
    }
}
