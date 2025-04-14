using Weltmeyer.RabbitMediator.Contracts.MessageBases;


namespace Weltmeyer.RabbitMediator.TestTool.Messages;

public class TestTargetedMessage:TargetedMessage
{
    public TimeSpan? Delay { get; set; }
    
}