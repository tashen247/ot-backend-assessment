using System.Data;
using Dapper;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Core.Messaging;

namespace OT.Assessment.Infrastructure.Persistence;

/// <summary>Writes a batch of wagers through one stored procedure call using a table-valued parameter.</summary>
internal sealed class SqlCasinoWagerWriter : ICasinoWagerWriter
{
    private const int CommandTimeoutSeconds = 60;
    private readonly ISqlConnectionFactory _connectionFactory;

    public SqlCasinoWagerWriter(ISqlConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public async Task<int> WriteBatchAsync(IReadOnlyCollection<CasinoWagerMessage> wagers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wagers);
        if (wagers.Count == 0)
        {
            return 0;
        }

        var parameters = new DynamicParameters();
        parameters.Add("@Wagers", CasinoWagerTableType.ToDataTable(wagers).AsTableValuedParameter(CasinoWagerTableType.TypeName));

        await using var connection = _connectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "dbo.usp_CasinoWager_InsertBatch",
            parameters,
            commandType: CommandType.StoredProcedure,
            commandTimeout: CommandTimeoutSeconds,
            cancellationToken: cancellationToken));
    }
}
