using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OT.Assessment.Consumer;
using OT.Assessment.Consumer.Processing;
using OT.Assessment.Core.Abstractions;
using OT.Assessment.Core.Messaging;

namespace OT.Assessment.UnitTests.Consumer;

public class CasinoWagerBatchProcessorTests
{
    private readonly ICasinoWagerWriter _writer = Substitute.For<ICasinoWagerWriter>();

    private CasinoWagerBatchProcessor CreateProcessor(int retries = 2) => new(
        _writer,
        Options.Create(new ConsumerOptions { MaxWriteRetries = retries, RetryBaseDelayMilliseconds = 1 }),
        NullLogger<CasinoWagerBatchProcessor>.Instance);

    private static InboundMessage Delivery(ulong tag, CasinoWagerMessage wager) => new(tag, MessageSerializer.Serialize(wager));

    private static InboundMessage RawDelivery(ulong tag, string body) => new(tag, Encoding.UTF8.GetBytes(body));

    [Fact]
    public async Task Writes_a_valid_batch_in_one_call_and_acknowledges_every_delivery()
    {
        _writer.WriteBatchAsync(Arg.Any<IReadOnlyCollection<CasinoWagerMessage>>(), Arg.Any<CancellationToken>()).Returns(3);
        var batch = new[] { Delivery(1, TestData.Wager()), Delivery(2, TestData.Wager()), Delivery(3, TestData.Wager()) };

        var result = await CreateProcessor().ProcessAsync(batch, CancellationToken.None);

        await _writer.Received(1).WriteBatchAsync(Arg.Is<IReadOnlyCollection<CasinoWagerMessage>>(w => w.Count == 3), Arg.Any<CancellationToken>());
        Assert.Equal([1UL, 2UL, 3UL], result.Acknowledged);
        Assert.Empty(result.DeadLettered);
        Assert.Equal(3, result.Inserted);
    }

    [Fact]
    public async Task Dead_letters_malformed_json_and_stores_the_rest()
    {
        _writer.WriteBatchAsync(Arg.Any<IReadOnlyCollection<CasinoWagerMessage>>(), Arg.Any<CancellationToken>()).Returns(1);
        var batch = new[] { RawDelivery(1, "{not json"), Delivery(2, TestData.Wager()) };

        var result = await CreateProcessor().ProcessAsync(batch, CancellationToken.None);

        Assert.Equal([1UL], result.DeadLettered);
        Assert.Equal([2UL], result.Acknowledged);
    }

    [Fact]
    public async Task Dead_letters_messages_that_break_database_invariants()
    {
        var batch = new[] { Delivery(1, TestData.Wager(countryCode: "ZAF")), Delivery(2, TestData.Wager(amount: -1m)) };

        var result = await CreateProcessor().ProcessAsync(batch, CancellationToken.None);

        Assert.Equal([1UL, 2UL], result.DeadLettered);
        await _writer.DidNotReceiveWithAnyArgs().WriteBatchAsync(default, default);
    }

    [Fact]
    public async Task Isolates_a_row_the_database_rejects_instead_of_failing_the_batch()
    {
        var poison = TestData.Wager();
        _writer.WriteBatchAsync(Arg.Any<IReadOnlyCollection<CasinoWagerMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var wagers = call.Arg<IReadOnlyCollection<CasinoWagerMessage>>();
                if (wagers.Any(w => w.WagerId == poison.WagerId)) throw new InvalidOperationException("constraint violated");
                return wagers.Count;
            });
        var batch = new[] { Delivery(1, TestData.Wager()), Delivery(2, poison), Delivery(3, TestData.Wager()) };

        var result = await CreateProcessor().ProcessAsync(batch, CancellationToken.None);

        Assert.Equal([1UL, 3UL], result.Acknowledged);
        Assert.Equal([2UL], result.DeadLettered);
        Assert.Equal(2, result.Inserted);
    }

    [Fact]
    public async Task Retries_transient_failures_then_succeeds()
    {
        _writer.WriteBatchAsync(Arg.Any<IReadOnlyCollection<CasinoWagerMessage>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new TimeoutException(), _ => 1);

        var result = await CreateProcessor().ProcessAsync([Delivery(1, TestData.Wager())], CancellationToken.None);

        Assert.Equal([1UL], result.Acknowledged);
        await _writer.ReceivedWithAnyArgs(2).WriteBatchAsync(default, default);
    }

    [Fact]
    public async Task Throws_when_transient_failures_outlast_the_retries_so_the_batch_is_held()
    {
        _writer.WriteBatchAsync(Arg.Any<IReadOnlyCollection<CasinoWagerMessage>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException());

        await Assert.ThrowsAsync<TimeoutException>(() =>
            CreateProcessor(retries: 2).ProcessAsync([Delivery(1, TestData.Wager())], CancellationToken.None));

        await _writer.ReceivedWithAnyArgs(3).WriteBatchAsync(default, default);
    }

    [Fact]
    public async Task Duplicates_are_acknowledged_but_not_counted_as_inserted()
    {
        _writer.WriteBatchAsync(Arg.Any<IReadOnlyCollection<CasinoWagerMessage>>(), Arg.Any<CancellationToken>()).Returns(1);
        var wager = TestData.Wager();

        var result = await CreateProcessor().ProcessAsync([Delivery(1, wager), Delivery(2, wager)], CancellationToken.None);

        Assert.Equal([1UL, 2UL], result.Acknowledged);
        Assert.Equal(1, result.Inserted);
    }
}
