using Weltmeyer.RabbitMediator.TestTool.Messages;

namespace Weltmeyer.RabbitMediator.TestTool.Consumers;

public class TestAnyTargetedRequestDerivedConsumer : TestAnyTargetedAbstractConsumerBase<
    TestAnyTargetedRequestForAbstract, TestAnyTargetedResponseForAbstract>
{
    public long ReceivedMessages;

    public override Task<TestAnyTargetedResponseForAbstract> Consume(TestAnyTargetedRequestForAbstract message)
    {
        Interlocked.Increment(ref ReceivedMessages);
        if (message.CrashPlease)
            throw new TestException();
        return Task.FromResult(new TestAnyTargetedResponseForAbstract
            { TestRequiredString = DateTimeOffset.Now.ToString() });
    }
}