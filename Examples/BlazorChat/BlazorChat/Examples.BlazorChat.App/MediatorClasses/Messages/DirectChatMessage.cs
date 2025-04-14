namespace Examples.BlazorChat.App.MediatorClasses.Messages;

public class DirectChatMessage : Weltmeyer.RabbitMediator.Contracts.MessageBases.TargetedMessage, IChatMessage
{
    public string SenderName { get; set; }
    public string Message { get; set; }
}