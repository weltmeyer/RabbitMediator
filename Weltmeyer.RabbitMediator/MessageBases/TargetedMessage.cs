using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts;

namespace Weltmeyer.RabbitMediator.MessageBases;

public abstract class TargetedMessage : Message, ITargetedSentObject
{
    [JsonInclude] public required InstanceInformation TargetInstance { get;  init; }
}