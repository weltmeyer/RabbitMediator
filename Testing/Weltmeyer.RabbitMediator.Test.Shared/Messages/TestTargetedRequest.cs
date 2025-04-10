using Weltmeyer.RabbitMediator.MessageBases;

namespace Weltmeyer.RabbitMediator.TestTool.Messages;

public class TestTargetedRequest:TargetedRequest<TestTargetedResponse>
{
    public TimeSpan? Delay { get; set; }
}

public class TestTargetedResponse : Response
{
    public required string TestRequiredString { get; init; }
}