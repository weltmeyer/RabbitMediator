using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts;

namespace Weltmeyer.RabbitMediator.MessageBases;

public abstract class Response : Message, ITargetedSentObject
{
    [JsonInclude] public Guid RequestId { get; internal set; }
    [JsonInclude] public override Guid SenderId { get; internal set; }
    [JsonInclude] public Guid TargetId { get; internal set; }
    [JsonInclude] public override Guid SentId { get; internal set; }
    [JsonInclude] public override bool RequireAck { get; internal set; }
    
    [JsonInclude] public bool Success { get; internal set; }
    
    [JsonInclude] public ExceptionData? ExceptionData { get; internal set; }
    
    public bool TimedOut { get; internal set; }
    /// <summary>
    /// Happens only on targeted messages, when we cant route the message to the targed
    /// </summary>
    public bool SendFailure { get; internal set; }
}