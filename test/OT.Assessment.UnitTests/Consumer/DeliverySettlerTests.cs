using System.Text;
using OT.Assessment.Consumer.Processing;
using OT.Assessment.Core.Abstractions;

namespace OT.Assessment.UnitTests.Consumer;

public class DeliverySettlerTests
{
    private readonly RecordingAcker _acker = new();
    private readonly RecordingDeadLetter _deadLetters = new();

    private DeliverySettler Settler => new(_deadLetters);

    [Fact]
    public async Task Settles_only_the_listed_tags_when_the_batch_is_not_a_contiguous_prefix()
    {
        var batch = new List<InboundMessage> { Message(1), Message(3), Message(10) };
        var result = new BatchResult();
        result.Acknowledged.Add(1);
        result.Acknowledged.Add(10);
        result.DeadLetter(3, "malformed message");

        await Settler.SettleAsync(batch, result, _acker, CancellationToken.None);

        Assert.Equal("body-3", _deadLetters.Published.Single().Body);
        Assert.Equal("malformed message", _deadLetters.Published.Single().Reason);
        Assert.Equal(["ack:3", "ack:1", "ack:10"], _acker.Calls);
    }

    [Fact]
    public async Task Does_not_ack_a_poison_message_when_the_dead_letter_publish_fails()
    {
        _deadLetters.Fail = true;
        var result = new BatchResult();
        result.DeadLetter(3, "malformed message");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Settler.SettleAsync([Message(3)], result, _acker, CancellationToken.None));

        Assert.Empty(_acker.Calls);
    }

    [Fact]
    public async Task Requeues_each_delivery_in_the_batch_on_its_own_tag()
    {
        await Settler.RequeueAsync([Message(1), Message(3), Message(10)], _acker, CancellationToken.None);

        Assert.Equal(["requeue:1", "requeue:3", "requeue:10"], _acker.Calls);
    }

    private static InboundMessage Message(ulong tag) => new(tag, Encoding.UTF8.GetBytes($"body-{tag}"));

    private sealed class RecordingAcker : IDeliveryAcker
    {
        public List<string> Calls { get; } = [];

        public Task AckAsync(ulong deliveryTag, CancellationToken cancellationToken)
        {
            Calls.Add($"ack:{deliveryTag}");
            return Task.CompletedTask;
        }

        public Task RequeueAsync(ulong deliveryTag, CancellationToken cancellationToken)
        {
            Calls.Add($"requeue:{deliveryTag}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDeadLetter : IDeadLetterPublisher
    {
        public List<(string Body, string Reason)> Published { get; } = [];
        public bool Fail { get; set; }

        public Task PublishConfirmedAsync(ReadOnlyMemory<byte> body, string reason, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("broker rejected the dead-letter publish");
            }

            Published.Add((Encoding.UTF8.GetString(body.Span), reason));
            return Task.CompletedTask;
        }
    }
}
