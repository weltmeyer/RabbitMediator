using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.MessageBases;

public abstract class Message : IMessage
{
    [JsonInclude] public abstract Guid SenderId { get; internal set; }
    [JsonInclude] public abstract Guid SentId { get; internal set; }
    [JsonInclude] public abstract bool RequireAck { get; internal set; }
}

internal interface IMessage : ISentObject
{
    [JsonInclude]
    public bool RequireAck { get;  }
}