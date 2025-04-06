using Weltmeyer.RabbitMediator.ConsumerBases;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.TestTool.Consumers;

public class TestBroadCastMessageConsumer : IMessageConsumer<TestBroadcastMessage>
{
    public long ReceivedMessages;
    public Task Consume(TestBroadcastMessage message)
    {
        Interlocked.Increment(ref ReceivedMessages);

        return Task.CompletedTask;
    }
}