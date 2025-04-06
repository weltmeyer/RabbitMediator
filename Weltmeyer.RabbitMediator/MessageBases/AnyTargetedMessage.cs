using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.MessageBases;

public abstract class AnyTargetedMessage : Message, IAnyTargetedSentObject
{
    [JsonInclude] public override Guid SenderId { get; internal set; }
    [JsonInclude] public override Guid SentId { get; internal set; }
    [JsonInclude] public override bool RequireAck { get; internal set; }
}