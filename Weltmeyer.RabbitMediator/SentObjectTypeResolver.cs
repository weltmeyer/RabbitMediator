using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Weltmeyer.RabbitMediator.Contracts.MessageBases;

namespace Weltmeyer.RabbitMediator;

internal class SentObjectTypeResolver(Type[] sentObjectTypes) : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var requestedTypeInfo = base.GetTypeInfo(type, options);
        if (type != typeof(ISentObject))
            return requestedTypeInfo;


        requestedTypeInfo.PolymorphismOptions = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "$type",
            IgnoreUnrecognizedTypeDiscriminators = true,
            UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization
        };

        var derivedTypes = sentObjectTypes.Select(dt => new JsonDerivedType(dt, dt.FullName!));
        foreach (var cachedDerivedType in derivedTypes)
        {
            requestedTypeInfo.PolymorphismOptions.DerivedTypes.Add(cachedDerivedType);
        }


        return requestedTypeInfo;
    }
}