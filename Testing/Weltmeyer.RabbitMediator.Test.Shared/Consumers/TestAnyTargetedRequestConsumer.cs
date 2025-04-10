using Weltmeyer.RabbitMediator.ConsumerBases;
using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.TestTool.Consumers;

public class TestAnyTargetedRequestConsumer : IRequestConsumer<TestAnyTargetedRequest, TestAnyTargetedResponse>
{
    public long ReceivedMessages;

    public Task<TestAnyTargetedResponse> Consume(TestAnyTargetedRequest message)
    {
        Interlocked.Increment(ref ReceivedMessages);
        if(message.CrashPlease)
            throw new TestException();
        return Task.FromResult(new TestAnyTargetedResponse{TestRequiredString = DateTimeOffset.Now.ToString()});
    }
}