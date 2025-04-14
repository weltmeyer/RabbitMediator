using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts.Contracts;

namespace Weltmeyer.RabbitMediator.Contracts.MessageBases;

public abstract class Message : IMessage
{
    [JsonInclude] public InstanceInformation SenderInstance { get; internal set; } = null!;
    [JsonInclude] public bool RequireAck { get; internal set; }
    
    [JsonInclude] public Guid CorrelationId { get; internal set; }

}

internal interface IMessage : ISentObject
{
    [JsonInclude] public bool RequireAck { get; }
}