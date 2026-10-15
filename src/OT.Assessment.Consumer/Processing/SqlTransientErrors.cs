using Microsoft.Data.SqlClient;

namespace OT.Assessment.Consumer.Processing;

/// <summary>Classifies database exceptions that are worth retrying.</summary>
internal static class SqlTransientErrors
{
    // Timeout, deadlock victim, network/connection failures and Azure SQL failover codes.
    private static readonly HashSet<int> TransientNumbers =
        [-2, 20, 53, 64, 121, 233, 1205, 4060, 4221, 10053, 10054, 10060, 10928, 10929, 11001, 40143, 40197, 40501, 40613, 49918, 49919, 49920];

    public static bool IsTransient(Exception exception) => exception switch
    {
        SqlException sql => sql.Errors.Cast<SqlError>().Any(e => TransientNumbers.Contains(e.Number)),
        TimeoutException => true,
        _ => false
    };
}
