using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts;

namespace Weltmeyer.RabbitMediator.MessageBases;

public abstract class TargetedRequest<TResponse> : Request<TResponse>, ITargetedSentObject
    where TResponse : Response
{
    [JsonInclude] public required InstanceInformation TargetInstance { get;  init; }
}