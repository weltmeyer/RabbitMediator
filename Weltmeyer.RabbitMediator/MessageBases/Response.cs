using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts;

namespace Weltmeyer.RabbitMediator.MessageBases;

public abstract class Response : Message, ITargetedSentObject
{
    [JsonInclude] public InstanceInformation TargetInstance { get; internal set; }
   
    [JsonInclude] public bool Success { get; internal set; }
    
    [JsonInclude] public ExceptionData? ExceptionData { get; internal set; }
    
    public bool TimedOut { get; internal set; }
    /// <summary>
    /// Happens only on targeted messages, when we cant route the message to the targed
    /// </summary>
    public bool SendFailure { get; internal set; }
}