using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator.TestTool.Messages;

public class TestAnyTargetedRequest:AnyTargetedRequest<TestAnyTargetedResponse>
{
    public bool CrashPlease { get; set; }
}

public class TestAnyTargetedResponse : Response
{
    public required string TestRequiredString { get; init; }
}