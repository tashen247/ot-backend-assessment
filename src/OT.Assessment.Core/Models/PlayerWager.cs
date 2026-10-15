namespace OT.Assessment.Core.Models;

/// <summary>A wager as returned by GET api/player/{playerId}/casino.</summary>
public sealed class PlayerWager
{
    public Guid WagerId { get; init; }
    public string Game { get; init; }
    public string Provider { get; init; }
    public decimal Amount { get; init; }
    public DateTimeOffset CreatedDate { get; init; }
}
