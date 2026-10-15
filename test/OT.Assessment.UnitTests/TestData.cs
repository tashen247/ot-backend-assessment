using OT.Assessment.Core.Messaging;

namespace OT.Assessment.UnitTests;

internal static class TestData
{
    public static CasinoWagerMessage Wager(Guid? accountId = null, decimal amount = 100m, DateTimeOffset? created = null,
        string countryCode = "ZA", string provider = "Provider A", string game = "Game A") => new()
    {
        WagerId = Guid.NewGuid(),
        Theme = "adventure",
        Provider = provider,
        GameName = game,
        TransactionId = Guid.NewGuid(),
        BrandId = Guid.NewGuid(),
        AccountId = accountId ?? Guid.NewGuid(),
        Username = "test.player",
        ExternalReferenceId = Guid.NewGuid(),
        TransactionTypeId = Guid.NewGuid(),
        Amount = amount,
        CreatedDateTime = created ?? DateTimeOffset.UtcNow,
        NumberOfBets = 1,
        CountryCode = countryCode,
        SessionData = "session",
        Duration = 1000
    };

    /// <summary>The sample payload from the assessment brief, with the brief's casing ("Username", "Duration").</summary>
    public const string BriefSampleJson = """
    {
      "wagerId": "aa6700eb-1a06-483e-9739-d293dc7a9383",
      "theme": "adventure",
      "provider": "Ergonomic Soft Fish",
      "gameName": "Ergonomic Granite Cheese",
      "transactionId": "410b7161-3473-4d74-85c3-a533d050a9d3",
      "brandId": "8a2016f8-c4c4-471f-9a9c-337a54664650",
      "accountId": "5ac75fec-23e9-27d1-b660-179eee70003d",
      "Username": "Jay.Bernhard67",
      "externalReferenceId": "0267dbca-2760-4a9e-ab42-5ce766fa8ca0",
      "transactionTypeId": "8aaece0c-5d53-4225-a937-adb454c4da31",
      "amount": 38273.974454660885,
      "createdDateTime": "2024-05-04T02:25:05.9906387+02:00",
      "numberOfBets": 3,
      "countryCode": "BS",
      "sessionData": "Central Chile Awesome Cotton Gloves",
      "Duration": 1827254
    }
    """;
}
