namespace Examples.BlazorChat.App.MediatorClasses.Messages;

public class DirectChatMessage : Weltmeyer.RabbitMediator.MessageBases.TargetedMessage, IChatMessage
{
    public string SenderName { get; set; }
    public string Message { get; set; }
}