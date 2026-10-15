using OT.Assessment.Consumer.Processing;
using OT.Assessment.Core.Messaging;

namespace OT.Assessment.UnitTests.Consumer;

public class CasinoWagerMessageRulesTests
{
    [Fact]
    public void Accepts_a_complete_wager() => Assert.True(CasinoWagerMessageRules.IsValid(TestData.Wager(), out _));

    public static TheoryData<CasinoWagerMessage, string> InvalidWagers => new()
    {
        { TestData.Wager() with { WagerId = Guid.Empty }, "wagerId" },
        { TestData.Wager() with { AccountId = Guid.Empty }, "accountId" },
        { TestData.Wager() with { Username = "" }, "username" },
        { TestData.Wager() with { NumberOfBets = 0 }, "numberOfBets" },
        { TestData.Wager(countryCode: null), "countryCode" },
        { TestData.Wager(countryCode: "Z"), "countryCode" },
        { TestData.Wager(amount: -0.01m), "amount" },
        { TestData.Wager(provider: ""), "provider" }
    };

    [Theory]
    [MemberData(nameof(InvalidWagers))]
    public void Rejects_wagers_the_database_cannot_store(CasinoWagerMessage wager, string expectedField)
    {
        Assert.False(CasinoWagerMessageRules.IsValid(wager, out var reason));
        Assert.Contains(expectedField, reason);
    }
}
