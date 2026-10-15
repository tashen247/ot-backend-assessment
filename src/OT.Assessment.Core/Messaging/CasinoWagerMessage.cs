namespace OT.Assessment.Core.Messaging;

/// <summary>
/// The casino wager event as it travels over the message bus. The full event is carried
/// (including fields the database does not store, such as <see cref="SessionData"/>) so the
/// queue remains a faithful record of what was received and new consumers can use it later.
/// </summary>
public sealed record CasinoWagerMessage
{
    public Guid WagerId { get; init; }
    public string Theme { get; init; }
    public string Provider { get; init; }
    public string GameName { get; init; }
    public Guid TransactionId { get; init; }
    public Guid BrandId { get; init; }
    public Guid AccountId { get; init; }
    public string Username { get; init; }
    public Guid ExternalReferenceId { get; init; }
    public Guid TransactionTypeId { get; init; }
    public decimal Amount { get; init; }
    public DateTimeOffset CreatedDateTime { get; init; }
    public int NumberOfBets { get; init; }
    public string CountryCode { get; init; }
    public string SessionData { get; init; }
    public long Duration { get; init; }
}
