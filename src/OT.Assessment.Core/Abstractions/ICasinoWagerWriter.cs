using OT.Assessment.Core.Messaging;

namespace OT.Assessment.Core.Abstractions;

/// <summary>Persists consumed wagers.</summary>
public interface ICasinoWagerWriter
{
    /// <summary>
    /// Stores a batch of wagers in one round trip. Idempotent: wagers that are already
    /// stored are skipped.
    /// </summary>
    /// <returns>The number of wagers newly stored.</returns>
    Task<int> WriteBatchAsync(IReadOnlyCollection<CasinoWagerMessage> wagers, CancellationToken cancellationToken);
}
