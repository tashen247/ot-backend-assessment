using System.Threading.Channels;

namespace OT.Assessment.Consumer.Processing;

internal static class BatchReader
{
    /// <summary>
    /// Waits for at least one item, then keeps collecting until <paramref name="maxSize"/> items
    /// are in hand or <paramref name="window"/> has elapsed since the first item arrived.
    /// Under load batches fill to <paramref name="maxSize"/>; when traffic is light a single
    /// wager is still written within <paramref name="window"/>.
    /// </summary>
    /// <exception cref="ChannelClosedException">The channel completed and no items remain.</exception>
    public static async Task<List<T>> ReadBatchAsync<T>(ChannelReader<T> reader, int maxSize, TimeSpan window, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, 1);

        var batch = new List<T>(maxSize) { await reader.ReadAsync(cancellationToken) };

        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        windowCts.CancelAfter(window);

        try
        {
            while (batch.Count < maxSize)
            {
                while (batch.Count < maxSize && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count < maxSize && !await reader.WaitToReadAsync(windowCts.Token))
                {
                    break; // channel completed
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The batch window elapsed: return what has arrived.
        }

        return batch;
    }
}
