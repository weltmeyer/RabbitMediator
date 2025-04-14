using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts.Contracts;

namespace Weltmeyer.RabbitMediator.Contracts.MessageBases;

public abstract class TargetedRequest<TResponse> : Request<TResponse>, ITargetedSentObject
    where TResponse : Response
{
    [JsonInclude] public required InstanceInformation TargetInstance { get;  init; }
}