using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.MessageBases;

// ReSharper disable once UnusedTypeParameter
public abstract class Request<TResponse> : IRequest
    where TResponse : Response
{
    [JsonInclude]public abstract Guid SentId { get; internal set; }
    [JsonInclude] public abstract Guid SenderId { get; internal set; }
    [JsonInclude] public abstract Guid RequestId { get; internal set; }
}


internal interface IRequest : ISentObject
{
    [JsonInclude] internal Guid RequestId { get; }
};