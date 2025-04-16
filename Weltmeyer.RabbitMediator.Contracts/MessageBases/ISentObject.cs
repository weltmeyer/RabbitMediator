using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts.Contracts;

namespace Weltmeyer.RabbitMediator.Contracts.MessageBases;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal interface ISentObject
{
    [JsonInclude] public InstanceInformation SenderInstance { get; }

    [JsonInclude] public Guid CorrelationId { get; }
    
    [JsonInclude] public string? TelemetryTraceParent { get; }
    [JsonInclude] public string? TelemetryTraceState { get; }
    
    
}