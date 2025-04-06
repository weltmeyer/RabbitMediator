using System.Text.Json.Serialization;

namespace Weltmeyer.RabbitMediator.MessageBases;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
internal interface ISentObject
{
    [JsonInclude]
    public Guid SenderId { get;  }
    
    [JsonInclude]
    public Guid SentId { get;  }
    
}