using Weltmeyer.RabbitMediator.ConsumerBases;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.TestTool.Consumers;

public class TestAnyTargetedMessageConsumer : IMessageConsumer<TestAnyTargetedMessage>
{
    public long ReceivedMessages;

    public Task Consume(TestAnyTargetedMessage message)
    {
        Interlocked.Increment(ref ReceivedMessages);
        if (message.CrashPlease)
            throw new TestException();
        return Task.CompletedTask;
    }
}