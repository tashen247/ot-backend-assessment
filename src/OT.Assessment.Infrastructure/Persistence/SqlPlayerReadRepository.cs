using System.Data;
using Dapper;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Core.Models;

namespace OT.Assessment.Infrastructure.Persistence;

internal sealed class SqlPlayerReadRepository : IPlayerReadRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public SqlPlayerReadRepository(ISqlConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<PagedResult<PlayerWager>> GetCasinoWagersAsync(Guid accountId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.Create();
        await using var results = await connection.QueryMultipleAsync(new CommandDefinition(
            "dbo.usp_CasinoWager_GetByPlayer",
            new { AccountId = accountId, Page = page, PageSize = pageSize },
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        var total = await results.ReadSingleAsync<int>();
        var wagers = (await results.ReadAsync<PlayerWager>()).AsList();

        return new PagedResult<PlayerWager>(wagers, page, pageSize, total);
    }

    public async Task<IReadOnlyList<TopSpender>> GetTopSpendersAsync(int count, CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory.Create();
        var spenders = await connection.QueryAsync<TopSpender>(new CommandDefinition(
            "dbo.usp_Player_GetTopSpenders",
            new { Count = count },
            commandType: CommandType.StoredProcedure,
            cancellationToken: cancellationToken));

        return spenders.AsList();
    }
}
