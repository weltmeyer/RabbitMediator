using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.MessageBases;

public abstract class TargetedMessage : Message, ITargetedSentObject
{
    [JsonInclude] public required Guid TargetId { get; set; }
    [JsonInclude] public override Guid SenderId { get; internal set; }
    [JsonInclude] public override Guid SentId { get; internal set; }
    [JsonInclude] public override bool RequireAck { get; internal set; }
}