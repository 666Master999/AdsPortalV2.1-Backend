using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdsPortalV2.Services
{
    public class CamelCaseEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
        {
            var t = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
            return t.IsEnum;
        }

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var isNullable = Nullable.GetUnderlyingType(typeToConvert) != null;
            var enumType = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;

            var enumConverterType = typeof(CamelCaseEnumConverter<>).MakeGenericType(enumType);
            var enumConverter = (JsonConverter)Activator.CreateInstance(enumConverterType)!;

            if (!isNullable)
            {
                return enumConverter;
            }

            var nullableConverterType = typeof(NullableEnumConverter<>).MakeGenericType(enumType);
            return (JsonConverter)Activator.CreateInstance(nullableConverterType, enumConverter)!;
        }

        private class NullableEnumConverter<T> : JsonConverter<T?> where T : struct, Enum
        {
            private readonly JsonConverter<T> _inner;
            public NullableEnumConverter(JsonConverter<T> inner) => _inner = inner;

            public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Null)
                    return null;
                return _inner.Read(ref reader, typeof(T), options);
            }

            public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
            {
                if (!value.HasValue)
                {
                    writer.WriteNullValue();
                    return;
                }
                _inner.Write(writer, value.Value, options);
            }
        }
    }

    // Inline camel-case enum converter implementation to avoid a separate file.
    public class CamelCaseEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();

            if (value == null)
                throw new JsonException();

            if (Enum.TryParse<T>(value, ignoreCase: true, out var result))
                return result;

            throw new JsonException($"Invalid enum value: {value}");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var text = value.ToString();
            var camel = char.ToLowerInvariant(text[0]) + text.Substring(1);
            writer.WriteStringValue(camel);
        }
    }
}
