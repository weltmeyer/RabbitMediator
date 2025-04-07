using System.Text.Json.Serialization;
using Weltmeyer.RabbitMediator.Contracts;

namespace Weltmeyer.RabbitMediator.MessageBases;

internal interface ITargetedSentObject : ISentObject
{
    public InstanceInformation TargetInstance { get; }
  
}