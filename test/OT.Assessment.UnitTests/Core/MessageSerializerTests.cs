using System.Text;
using System.Text.Json;
using OT.Assessment.Core.Messaging;
using OT.Assessment.Core.Models;

namespace OT.Assessment.UnitTests.Core;

public class MessageSerializerTests
{
    [Fact]
    public void Round_trips_every_field()
    {
        var wager = TestData.Wager(amount: 1234.5678m);

        var copy = MessageSerializer.Deserialize(MessageSerializer.Serialize(wager));

        Assert.Equivalent(wager, copy, strict: true);
    }

    [Fact]
    public void Uses_camel_case_on_the_wire()
    {
        var json = Encoding.UTF8.GetString(MessageSerializer.Serialize(TestData.Wager()));

        Assert.Contains("\"wagerId\"", json);
        Assert.Contains("\"accountId\"", json);
    }

    [Fact]
    public void Rejects_a_null_body() =>
        Assert.Throws<JsonException>(() => MessageSerializer.Deserialize("null"u8));
}

public class PagedResultTests
{
    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(35, 10, 4)]
    [InlineData(40, 10, 4)]
    [InlineData(1, 10, 1)]
    public void Calculates_total_pages(int total, int pageSize, int expected) =>
        Assert.Equal(expected, new PagedResult<int>([], 1, pageSize, total).TotalPages);
}
