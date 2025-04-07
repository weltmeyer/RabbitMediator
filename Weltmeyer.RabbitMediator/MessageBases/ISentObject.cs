using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts;

namespace Weltmeyer.RabbitMediator.MessageBases;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal interface ISentObject
{
    [JsonInclude] public InstanceInformation SenderInstance { get; }

    [JsonInclude] public Guid CorrelationId { get; }
}