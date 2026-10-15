using System.Threading.Channels;
using OT.Assessment.Consumer.Processing;

namespace OT.Assessment.UnitTests.Consumer;

public class BatchReaderTests
{
    [Fact]
    public async Task Returns_a_full_batch_as_soon_as_max_size_is_reached()
    {
        var channel = Channel.CreateUnbounded<int>();
        for (var i = 0; i < 25; i++) channel.Writer.TryWrite(i);

        var batch = await BatchReader.ReadBatchAsync(channel.Reader, 10, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, 10), batch);
    }

    [Fact]
    public async Task Returns_a_partial_batch_when_the_window_elapses()
    {
        var channel = Channel.CreateUnbounded<int>();
        channel.Writer.TryWrite(1);
        channel.Writer.TryWrite(2);

        var batch = await BatchReader.ReadBatchAsync(channel.Reader, 100, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Equal([1, 2], batch);
    }

    [Fact]
    public async Task Waits_for_the_first_item_before_starting_the_window()
    {
        var channel = Channel.CreateUnbounded<int>();
        var read = BatchReader.ReadBatchAsync(channel.Reader, 100, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        await Task.Delay(150);
        Assert.False(read.IsCompleted);

        channel.Writer.TryWrite(7);
        Assert.Equal([7], await read);
    }

    [Fact]
    public async Task Returns_remaining_items_when_the_channel_completes()
    {
        var channel = Channel.CreateUnbounded<int>();
        channel.Writer.TryWrite(1);
        channel.Writer.Complete();

        Assert.Equal([1], await BatchReader.ReadBatchAsync(channel.Reader, 10, TimeSpan.FromSeconds(30), CancellationToken.None));
        await Assert.ThrowsAsync<ChannelClosedException>(() =>
            BatchReader.ReadBatchAsync(channel.Reader, 10, TimeSpan.FromSeconds(30), CancellationToken.None));
    }

    [Fact]
    public async Task Honours_cancellation_while_waiting()
    {
        var channel = Channel.CreateUnbounded<int>();
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BatchReader.ReadBatchAsync(channel.Reader, 10, TimeSpan.FromSeconds(30), cts.Token));
    }
}
