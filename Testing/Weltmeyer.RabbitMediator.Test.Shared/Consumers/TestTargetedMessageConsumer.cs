using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.TestTool.Consumers;

public class TestTargetedMessageConsumer : IMessageConsumer<TestTargetedMessage>
{
    public long ReceivedMessages;

    public async Task Consume(TestTargetedMessage message)
    {
        Interlocked.Increment(ref ReceivedMessages);
        
        if(message.Delay.HasValue)
            await Task.Delay(message.Delay.Value);

    }
}