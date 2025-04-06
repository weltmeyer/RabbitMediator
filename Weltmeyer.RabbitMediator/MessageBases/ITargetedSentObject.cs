using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.MessageBases;

internal interface ITargetedSentObject : ISentObject
{
    [JsonInclude] Guid TargetId { get; }
}