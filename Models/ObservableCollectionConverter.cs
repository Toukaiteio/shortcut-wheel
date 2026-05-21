using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShortcutWheel.Models;

public class ObservableCollectionConverter<T> : JsonConverter<ObservableCollection<T>>
{
    public override ObservableCollection<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var list = JsonSerializer.Deserialize<List<T>>(ref reader, options);
        return new ObservableCollection<T>(list ?? []);
    }

    public override void Write(Utf8JsonWriter writer, ObservableCollection<T> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.ToList(), options);
    }
}
