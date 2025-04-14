using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts.Contracts;

namespace Weltmeyer.RabbitMediator.Contracts.MessageBases;

public abstract class TargetedMessage : Message, ITargetedSentObject
{
    [JsonInclude] public required InstanceInformation TargetInstance { get;  init; }
}