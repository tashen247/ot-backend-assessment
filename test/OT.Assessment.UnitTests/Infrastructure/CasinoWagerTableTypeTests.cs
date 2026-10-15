using System.Text.RegularExpressions;
using OT.Assessment.Infrastructure.Persistence;

namespace OT.Assessment.UnitTests.Infrastructure;

public class CasinoWagerTableTypeTests
{
    [Fact]
    public void Columns_match_the_table_type_in_DatabaseGenerate_sql_in_name_and_order()
    {
        // A TVP is bound by ordinal position, so a column added to one side only would silently
        // shift values into the wrong columns. This test reads the real schema script.
        var script = File.ReadAllText(FindRepositoryFile("DatabaseGenerate.sql"));
        var typeBody = Regex.Match(script, @"CREATE TYPE dbo\.CasinoWagerTableType AS TABLE\s*\((?<body>.*?)\);", RegexOptions.Singleline)
            .Groups["body"].Value;
        var sqlColumns = typeBody.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .ToList();

        var table = CasinoWagerTableType.ToDataTable([]);

        Assert.NotEmpty(sqlColumns);
        Assert.Equal(sqlColumns, table.Columns.Cast<System.Data.DataColumn>().Select(c => c.ColumnName));
    }

    [Fact]
    public void Rounds_amounts_to_the_stored_scale()
    {
        var table = CasinoWagerTableType.ToDataTable([TestData.Wager(amount: 38273.974454660885m)]);

        Assert.Equal(38273.9745m, table.Rows[0]["Amount"]);
    }

    [Fact]
    public void Maps_a_missing_theme_to_DBNull()
    {
        var withoutTheme = TestData.Wager() with { Theme = null };

        Assert.Equal(DBNull.Value, CasinoWagerTableType.ToDataTable([withoutTheme]).Rows[0]["Theme"]);
    }

    private static string FindRepositoryFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"{name} not found above {AppContext.BaseDirectory}");
    }
}
