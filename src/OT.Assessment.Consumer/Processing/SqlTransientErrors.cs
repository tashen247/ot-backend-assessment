using System.ComponentModel;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;

namespace OT.Assessment.Consumer.Processing;

/// <summary>
/// Classifies database exceptions that are worth retrying. Anything transient is retried and,
/// if it persists, the batch is requeued; anything else is treated as bad data and dead-lettered.
/// So when in doubt about the connection, this must answer "transient": an outage must never
/// dead-letter valid wagers.
/// </summary>
internal static class SqlTransientErrors
{
    // Timeout, deadlock victim, lock timeout, database recovering/offline, network failures and
    // Azure SQL failover codes.
    private static readonly HashSet<int> TransientNumbers =
        [-2, 2, 20, 35, 53, 64, 121, 233, 922, 926, 942, 1205, 1222, 4060, 4221, 10053, 10054, 10060, 10061,
         10928, 10929, 11001, 18401, 40143, 40197, 40501, 40613, 49918, 49919, 49920];

    // Severity 20 and above ends the connection: the server is unreachable or the session was
    // killed (for example a failed pre-login handshake while SQL Server is stopping or starting).
    // That says nothing about the data being written.
    private const byte ConnectionFatalSeverity = 20;

    public static bool IsTransient(Exception exception) => exception switch
    {
        SqlException sql => sql.Class >= ConnectionFatalSeverity
                            || sql.Errors.Cast<SqlError>().Any(e => TransientNumbers.Contains(e.Number) || e.Class >= ConnectionFatalSeverity)
                            || sql.InnerException is SocketException or Win32Exception,
        TimeoutException or SocketException => true,
        _ => false
    };
}
