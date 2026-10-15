using OT.Assessment.Core.Models;

namespace OT.Assessment.Core.Abstractions;

/// <summary>Read side for the player endpoints.</summary>
public interface IPlayerReadRepository
{
    Task<PagedResult<PlayerWager>> GetCasinoWagersAsync(Guid accountId, int page, int pageSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<TopSpender>> GetTopSpendersAsync(int count, CancellationToken cancellationToken);
}
