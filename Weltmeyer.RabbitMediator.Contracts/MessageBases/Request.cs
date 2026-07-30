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

    /// <summary>
    /// How long the sender is willing to wait, travelling with the request so the receiving side can stop a
    /// consumer that outlives it. A duration rather than a deadline on purpose: the receiver measures it on
    /// its own clock, which keeps it independent of how well the hosts' clocks agree.
    /// Null when the request came from a sender that did not send one yet.
    /// </summary>
    [JsonInclude] public TimeSpan? TimeOut { get; internal set; }
}

internal interface IRequest : ISentObject
{
    [JsonInclude] public TimeSpan? TimeOut { get; }
};