using Weltmeyer.RabbitMediator.Contracts;

namespace Examples.BlazorChat.App;

public class StoredMessage
{
    public DateTimeOffset Timestamp { get; set; }
    public string Message { get; set; }
    public InstanceInformation Instance { get; set; }
}