namespace OT.Assessment.UnitTests.Integration;

/// <summary>
/// Runs only when OT_TEST_SQL_CONNECTION points at a database created by DatabaseGenerate.sql,
/// so the suite stays green on a machine without SQL Server.
/// </summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "OT_TEST_SQL_CONNECTION";

    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
        {
            Skip = $"Set {EnvironmentVariable} to run SQL Server integration tests.";
        }
    }
}
