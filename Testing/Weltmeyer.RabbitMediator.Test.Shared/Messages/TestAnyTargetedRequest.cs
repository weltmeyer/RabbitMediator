using Weltmeyer.RabbitMediator.MessageBases;

namespace Weltmeyer.RabbitMediator.TestTool.Messages;

public class TestAnyTargetedRequest:AnyTargetedRequest<TestAnyTargetedResponse>
{
    public bool CrashPlease { get; set; }
}

public class TestAnyTargetedResponse : Response
{
    
}