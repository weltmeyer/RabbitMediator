using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.MessageBases;

public abstract class TargetedRequest<TResponse> : Request<TResponse>, ITargetedSentObject
    where TResponse : Response, new()
{
    public required Guid TargetId { get; init; }
    [JsonInclude] public override Guid SenderId { get; internal set; }
    [JsonInclude] public override Guid RequestId { get; internal set; }
    [JsonInclude] public override Guid SentId { get; internal set; }
}