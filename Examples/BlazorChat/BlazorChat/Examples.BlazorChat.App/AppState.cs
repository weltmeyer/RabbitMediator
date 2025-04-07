using System.Collections.Concurrent;
using Weltmeyer.RabbitMediator.Contracts;

namespace Examples.BlazorChat.App;

public class AppState
{
    public ConcurrentDictionary<InstanceInformation, string> UserList = new();

    public async Task AddUserIfUnknown(string username, InstanceInformation userid)
    {
        if (!UserList.TryAdd(userid, username))
            return;
        await OnNewUser(new NewUserEventArgs { Username = username, UserId = userid });
    }

    public event AsyncEvent.AsyncEventHandler<NewUserEventArgs>? NewUser;


    public class NewUserEventArgs : EventArgs
    {
        public string Username { get; set; }
        public InstanceInformation UserId { get; set; }
    }

    protected virtual async Task OnNewUser(NewUserEventArgs e)
    {
        if(NewUser is not null)
            await NewUser.Invoke(this, e);
    }
}