namespace Examples.BlazorChat.App.MediatorClasses.Messages;

public interface IChatMessage
{
    public string SenderName { get; set; }
    public string Message { get; set; }
}