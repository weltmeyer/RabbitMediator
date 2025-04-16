using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator.TestTool.Messages;

public class TestAnyTargetedRequestForAbstract : AnyTargetedRequest<TestAnyTargetedResponseForAbstract>
{
    public bool CrashPlease { get; set; }
}

public class TestAnyTargetedResponseForAbstract : Response
{
    public required string TestRequiredString { get; init; }
}