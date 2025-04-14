using Weltmeyer.RabbitMediator.Contracts.ConsumerBases;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.TestTool.Consumers;

public class TestTargetedRequestConsumer : IRequestConsumer<TestTargetedRequest, TestTargetedResponse>
{
    public long ReceivedMessages;

    public async Task<TestTargetedResponse> Consume(TestTargetedRequest message)
    {
        Interlocked.Increment(ref ReceivedMessages);
        if (message.Delay.HasValue)
            await Task.Delay(message.Delay.Value);
        return new TestTargetedResponse { TestRequiredString = DateTimeOffset.Now.ToString() };
    }
}