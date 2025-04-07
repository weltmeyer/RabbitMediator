using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.MessageBases;

public abstract class AnyTargetedMessage : Message, IAnyTargetedSentObject
{
}