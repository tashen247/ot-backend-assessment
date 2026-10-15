using System.ComponentModel.DataAnnotations;
using OT.Assessment.Core.Messaging;

namespace OT.Assessment.App.Contracts;

/// <summary>
/// A casino wager event as posted by a game platform. Identifiers are nullable so that a
/// missing value is rejected by validation instead of silently becoming <see cref="Guid.Empty"/>.
/// </summary>
public sealed class CasinoWagerRequest
{
    [Required] public Guid? WagerId { get; set; }

    [StringLength(50)] public string Theme { get; set; }

    [Required, StringLength(200)] public string Provider { get; set; }

    [Required, StringLength(200)] public string GameName { get; set; }

    [Required] public Guid? TransactionId { get; set; }

    [Required] public Guid? BrandId { get; set; }

    [Required] public Guid? AccountId { get; set; }

    [Required, StringLength(100)] public string Username { get; set; }

    [Required] public Guid? ExternalReferenceId { get; set; }

    [Required] public Guid? TransactionTypeId { get; set; }

    /// <summary>Wager amount. Stored to four decimal places.</summary>
    [Required, Range(0d, 1_000_000_000_000d)] public double? Amount { get; set; }

    [Required] public DateTimeOffset? CreatedDateTime { get; set; }

    [Required, Range(1, int.MaxValue)] public int? NumberOfBets { get; set; }

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    [Required, StringLength(2, MinimumLength = 2)] public string CountryCode { get; set; }

    [StringLength(4000)] public string SessionData { get; set; }

    /// <summary>Session duration in milliseconds.</summary>
    [Required, Range(0L, long.MaxValue)] public long? Duration { get; set; }

    /// <summary>Maps a validated request to the bus message.</summary>
    public CasinoWagerMessage ToMessage() => new()
    {
        WagerId = WagerId!.Value,
        Theme = Theme,
        Provider = Provider,
        GameName = GameName,
        TransactionId = TransactionId!.Value,
        BrandId = BrandId!.Value,
        AccountId = AccountId!.Value,
        Username = Username,
        ExternalReferenceId = ExternalReferenceId!.Value,
        TransactionTypeId = TransactionTypeId!.Value,
        Amount = (decimal)Amount!.Value,
        CreatedDateTime = CreatedDateTime!.Value,
        NumberOfBets = NumberOfBets!.Value,
        CountryCode = CountryCode.ToUpperInvariant(),
        SessionData = SessionData,
        Duration = Duration!.Value
    };
}
