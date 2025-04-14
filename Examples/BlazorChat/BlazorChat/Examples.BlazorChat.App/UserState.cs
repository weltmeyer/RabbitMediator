using Weltmeyer.RabbitMediator.Contracts.Contracts;

namespace Examples.BlazorChat.App;

public class UserState
{
    public List<StoredMessage> Messages = new();

    public string? Username { get; set; }
    
    public async Task AddMessage(string message, InstanceInformation instance)
    {
        var newMessage = new StoredMessage { Message = message, Timestamp = DateTimeOffset.Now, Instance = instance };
        Messages.Add(newMessage);
        await OnNewMessage(new NewMessageEventArgs { Message = newMessage });
    }

    public event AsyncEvent.AsyncEventHandler<NewMessageEventArgs>? NewMessage;


    public class NewMessageEventArgs : EventArgs
    {
        public required StoredMessage Message { get; set; }
    }

    protected virtual async Task OnNewMessage(NewMessageEventArgs e)
    {
        if (NewMessage is not null)
            await NewMessage.Invoke(this, e);
    }
}