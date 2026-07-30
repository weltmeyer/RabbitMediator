using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts.Contracts;

namespace Weltmeyer.RabbitMediator.Contracts.MessageBases;

// ReSharper disable once UnusedTypeParameter
public abstract class Request<TResponse> : IRequest
    where TResponse : Response
{
    [JsonInclude] public InstanceInformation SenderInstance { get; internal set; } = null!;

    [JsonInclude] public Guid CorrelationId { get; internal set; }
    [JsonInclude] public string? TelemetryTraceParent { get;  internal set; }
    [JsonInclude] public string? TelemetryTraceState { get;  internal set; }
}

internal interface IRequest : ISentObject
{
};