using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts;

namespace Weltmeyer.RabbitMediator.MessageBases;

// ReSharper disable once UnusedTypeParameter
public abstract class Request<TResponse> : IRequest
    where TResponse : Response
{
    [JsonInclude] public InstanceInformation SenderInstance { get; internal set; } = null!;

    [JsonInclude] public Guid CorrelationId { get; internal set; }
}

internal interface IRequest : ISentObject
{
};