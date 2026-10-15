using System.Data;
using OT.Assessment.Core.Messaging;

namespace OT.Assessment.Infrastructure.Persistence;

/// <summary>
/// Builds the table-valued parameter for dbo.CasinoWagerTableType. Column order must match the
/// type definition in DatabaseGenerate.sql; a unit test guards this.
/// </summary>
internal static class CasinoWagerTableType
{
    public const string TypeName = "dbo.CasinoWagerTableType";

    /// <summary>Monetary values are stored as DECIMAL(19,4).</summary>
    public const int AmountScale = 4;

    public static DataTable ToDataTable(IEnumerable<CasinoWagerMessage> wagers)
    {
        var table = new DataTable();
        table.Columns.Add("WagerId", typeof(Guid));
        table.Columns.Add("AccountId", typeof(Guid));
        table.Columns.Add("Username", typeof(string));
        table.Columns.Add("ProviderName", typeof(string));
        table.Columns.Add("GameName", typeof(string));
        table.Columns.Add("Theme", typeof(string));
        table.Columns.Add("TransactionId", typeof(Guid));
        table.Columns.Add("TransactionTypeId", typeof(Guid));
        table.Columns.Add("BrandId", typeof(Guid));
        table.Columns.Add("ExternalReferenceId", typeof(Guid));
        table.Columns.Add("Amount", typeof(decimal));
        table.Columns.Add("NumberOfBets", typeof(int));
        table.Columns.Add("CountryCode", typeof(string));
        table.Columns.Add("DurationMs", typeof(long));
        table.Columns.Add("CreatedDateTime", typeof(DateTimeOffset));

        foreach (var w in wagers)
        {
            table.Rows.Add(
                w.WagerId,
                w.AccountId,
                w.Username,
                w.Provider,
                w.GameName,
                (object)w.Theme ?? DBNull.Value,
                w.TransactionId,
                w.TransactionTypeId,
                w.BrandId,
                w.ExternalReferenceId,
                Math.Round(w.Amount, AmountScale, MidpointRounding.AwayFromZero),
                w.NumberOfBets,
                w.CountryCode,
                w.Duration,
                w.CreatedDateTime);
        }

        return table;
    }
}
