namespace Examples.BlazorChat.App.MediatorClasses.Messages;

public class BroadcastChatMessage:Weltmeyer.RabbitMediator.Contracts.MessageBases.BroadcastMessage,IChatMessage
{
    public string SenderName { get; set; }
    public string Message { get; set; }
}