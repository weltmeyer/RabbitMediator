using System.Buffers;
using System.Text.Json;
using Weltmeyer.RabbitMediator.MessageBases;

namespace Weltmeyer.RabbitMediator;

internal class JsonSerializerHelper
{
    private JsonSerializerOptions? _options;
    private readonly List<Type> _knownTypes = [];

    private JsonSerializerOptions RebuildOptions()
    {
        return new JsonSerializerOptions
        {
            TypeInfoResolver = new SentObjectTypeResolver(_knownTypes.ToArray()),
            
        };
    }

    public void AddTypeIfMissing(Type newType)
    {
        if (_knownTypes.Contains(newType))
            return;
        _knownTypes.Add(newType);
        _options = RebuildOptions();

    }

    /// <summary>
    /// Serializes the given object and calls action with the utf8 encoded byte data 
    /// </summary>
    /// <param name="objectToSerialize"></param>
    /// <param name="action"></param>
    /// <typeparam name="T"></typeparam>
    public async Task Serialize<T>(T objectToSerialize, Func<ReadOnlyMemory<byte>, Task> action)
        where T : class
    {
        _options ??= RebuildOptions();
        using var stream = new MemoryStream();
        if (objectToSerialize is ISentObject sentObject)
        {
            //this looks strange but is needed for the polymorphic $type setting in the json
            AddTypeIfMissing(objectToSerialize.GetType());
            await JsonSerializer.SerializeAsync(stream, sentObject, _options);
        }
        else
        {
            await JsonSerializer.SerializeAsync(stream, objectToSerialize, _options);
        }

        using var buffer = MemoryPool<byte>.Shared.Rent((int)stream.Length);
        stream.Position = 0;
        _ = await stream.ReadAsync(buffer.Memory);
        await action(buffer.Memory.Slice(0, (int)stream.Length));
    }

    public Task<T> Deserialize<T>(ReadOnlyMemory<byte> input)
    {
        var jsonUtfReader = new Utf8JsonReader(input.Span);

        var obj = JsonSerializer.Deserialize<T>(ref jsonUtfReader, _options)!;
        return Task.FromResult(obj);
    }
}