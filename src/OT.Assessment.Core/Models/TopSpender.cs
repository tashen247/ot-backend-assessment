namespace OT.Assessment.Core.Models;

/// <summary>A player as returned by GET api/player/topSpenders.</summary>
public sealed class TopSpender
{
    public Guid AccountId { get; init; }
    public string Username { get; init; }
    public decimal TotalAmountSpend { get; init; }
}
