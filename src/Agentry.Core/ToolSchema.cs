using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentry;

/// <summary>
/// Dependency-free JSON-Schema generation from a C# input type, plus the canonical
/// <see cref="JsonSerializerOptions"/> used to (de)serialize tool arguments.
/// Honors <see cref="DescriptionAttribute"/>, enums, nested objects, collections, and nullability.
/// </summary>
public static class ToolSchema
{
    /// <summary>Canonical options for tool argument (de)serialization (camelCase, case-insensitive, enums as strings).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly ConcurrentDictionary<Type, JsonElement> Cache = new();
    private static readonly NullabilityInfoContext NullCtx = new();

    /// <summary>Get (and cache) the JSON-Schema object for <typeparamref name="T"/>.</summary>
    public static object For<T>() => For(typeof(T));

    /// <summary>Get (and cache) the JSON-Schema object for <paramref name="type"/>.</summary>
    public static JsonElement For(Type type) => Cache.GetOrAdd(type, BuildObjectSchema);

    private static JsonElement BuildObjectSchema(Type type)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();

        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            var name = JsonName(p);
            properties[name] = PropertySchema(p);
            if (IsRequired(p)) required.Add(name);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0) schema["required"] = required;

        return JsonSerializer.SerializeToElement(schema, JsonOptions);
    }

    private static object PropertySchema(PropertyInfo p)
    {
        var node = TypeSchema(p.PropertyType);
        var description = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
        if (description is not null && node is Dictionary<string, object> dict)
            dict["description"] = description;
        return node;
    }

    private static object TypeSchema(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string)) return new Dictionary<string, object> { ["type"] = "string" };
        if (type == typeof(bool)) return new Dictionary<string, object> { ["type"] = "boolean" };
        if (type == typeof(Guid)) return new Dictionary<string, object> { ["type"] = "string", ["format"] = "uuid" };
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return new Dictionary<string, object> { ["type"] = "string", ["format"] = "date-time" };

        if (type.IsEnum)
            return new Dictionary<string, object> { ["type"] = "string", ["enum"] = Enum.GetNames(type) };

        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
            return new Dictionary<string, object> { ["type"] = "integer" };
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
            return new Dictionary<string, object> { ["type"] = "number" };

        var element = GetEnumerableElement(type);
        if (element is not null)
            return new Dictionary<string, object> { ["type"] = "array", ["items"] = TypeSchema(element) };

        // Nested object
        if (type.IsClass)
            return BuildObjectSchema(type);

        return new Dictionary<string, object> { ["type"] = "string" };
    }

    private static Type? GetEnumerableElement(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        if (!typeof(IEnumerable).IsAssignableFrom(type)) return null;
        foreach (var i in type.GetInterfaces())
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return i.GetGenericArguments()[0];
        return null;
    }

    private static string JsonName(PropertyInfo p)
    {
        var attr = p.GetCustomAttribute<JsonPropertyNameAttribute>();
        if (attr is not null) return attr.Name;
        return JsonNamingPolicy.CamelCase.ConvertName(p.Name);
    }

    private static bool IsRequired(PropertyInfo p)
    {
        if (Nullable.GetUnderlyingType(p.PropertyType) is not null) return false;
        if (p.PropertyType.IsValueType) return true;
        return NullCtx.Create(p).WriteState != NullabilityState.Nullable;
    }
}
