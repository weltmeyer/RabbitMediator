using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator.TestTool.Messages;

public class TestAnyTargetedMessage : AnyTargetedMessage
{
    public bool CrashPlease { get; set; }
}