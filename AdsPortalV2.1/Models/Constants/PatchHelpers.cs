using System.Text.Json;

namespace AdsPortalV2.Models;

public static class PatchHelpers
{
    public static void UpdateString(
        JsonElement raw,
        string currentValue,
        Action<string> setter,
        string fieldName,
        ICollection<string> updated,
        ICollection<string> skipped,
        ICollection<PatchErrorDto> errors,
        bool required = false)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
            return;
        }

        if (raw.ValueKind is not JsonValueKind.String)
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a string."));
            return;
        }

        var value = raw.GetString()?.Trim();
        if (required && string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be empty."));
            return;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be empty."));
            return;
        }

        if (string.Equals(value, currentValue, StringComparison.OrdinalIgnoreCase))
        {
            skipped.Add(fieldName);
            return;
        }

        setter(value);
        updated.Add(fieldName);
    }

    public static void UpdateInt(
        int value,
        int currentValue,
        Action<int> setter,
        string fieldName,
        ICollection<string> updated,
        ICollection<string> skipped,
        ICollection<PatchErrorDto> errors)
    {
        if (value == currentValue)
        {
            skipped.Add(fieldName);
            return;
        }

        setter(value);
        updated.Add(fieldName);
    }

    public static void UpdateNullableString(
        JsonElement raw,
        string? currentValue,
        Action<string?> setter,
        string fieldName,
        ICollection<string> updated,
        ICollection<string> skipped,
        ICollection<PatchErrorDto> errors,
        bool allowNull = true,
        bool required = false)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            if (!allowNull)
            {
                errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
                return;
            }

            if (currentValue is null)
            {
                skipped.Add(fieldName);
                return;
            }

            setter(null);
            updated.Add(fieldName);
            return;
        }

        if (raw.ValueKind is not JsonValueKind.String)
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a string."));
            return;
        }

        var value = raw.GetString()?.Trim();
        if (required && string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be empty."));
            return;
        }

        if (string.Equals(value, currentValue, StringComparison.OrdinalIgnoreCase))
        {
            skipped.Add(fieldName);
            return;
        }

        setter(value);
        updated.Add(fieldName);
    }

    public static void UpdateInt(
        JsonElement raw,
        int currentValue,
        Action<int> setter,
        string fieldName,
        ICollection<string> updated,
        ICollection<string> skipped,
        ICollection<PatchErrorDto> errors)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
            return;
        }

        if (!raw.TryGetInt32(out var value))
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be an integer."));
            return;
        }

        if (value == currentValue)
        {
            skipped.Add(fieldName);
            return;
        }

        setter(value);
        updated.Add(fieldName);
    }

    public static void UpdateNullableInt(
        JsonElement raw,
        int? currentValue,
        Action<int?> setter,
        string fieldName,
        ICollection<string> updated,
        ICollection<string> skipped,
        ICollection<PatchErrorDto> errors,
        bool allowNull = true)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            if (!allowNull)
            {
                errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
                return;
            }

            if (currentValue is null)
            {
                skipped.Add(fieldName);
                return;
            }

            setter(null);
            updated.Add(fieldName);
            return;
        }

        if (!raw.TryGetInt32(out var value))
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be an integer."));
            return;
        }

        if (EqualityComparer<int?>.Default.Equals(value, currentValue))
        {
            skipped.Add(fieldName);
            return;
        }

        setter(value);
        updated.Add(fieldName);
    }

    public static void UpdateDecimal(
        JsonElement raw,
        decimal currentValue,
        Action<decimal> setter,
        string fieldName,
        ICollection<string> updated,
        ICollection<string> skipped,
        ICollection<PatchErrorDto> errors)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
            return;
        }

        if (!raw.TryGetDecimal(out var value))
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a decimal."));
            return;
        }

        if (value == currentValue)
        {
            skipped.Add(fieldName);
            return;
        }

        setter(value);
        updated.Add(fieldName);
    }

    public static void UpdateNullableDecimal(
        JsonElement raw,
        decimal? currentValue,
        Action<decimal?> setter,
        string fieldName,
        ICollection<string> updated,
        ICollection<string> skipped,
        ICollection<PatchErrorDto> errors,
        bool allowNull = true)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            if (!allowNull)
            {
                errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
                return;
            }

            if (currentValue is null)
            {
                skipped.Add(fieldName);
                return;
            }

            setter(null);
            updated.Add(fieldName);
            return;
        }

        if (!raw.TryGetDecimal(out var value))
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a decimal."));
            return;
        }

        if (EqualityComparer<decimal?>.Default.Equals(value, currentValue))
        {
            skipped.Add(fieldName);
            return;
        }

        setter(value);
        updated.Add(fieldName);
    }

    public static void UpdateBool(
        JsonElement raw,
        bool currentValue,
        Action<bool> setter,
        string fieldName,
        ICollection<string> updated,
        ICollection<string> skipped,
        ICollection<PatchErrorDto> errors)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
            return;
        }

        if (raw.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a boolean."));
            return;
        }

        var value = raw.GetBoolean();
        if (value == currentValue)
        {
            skipped.Add(fieldName);
            return;
        }

        setter(value);
        updated.Add(fieldName);
    }

    public static void UpdateNullableBool(
        JsonElement raw,
        bool? currentValue,
        Action<bool?> setter,
        string fieldName,
        ICollection<string> updated,
        ICollection<string> skipped,
        ICollection<PatchErrorDto> errors,
        bool allowNull = true)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            if (!allowNull)
            {
                errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
                return;
            }

            if (currentValue is null)
            {
                skipped.Add(fieldName);
                return;
            }

            setter(null);
            updated.Add(fieldName);
            return;
        }

        if (raw.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            errors.Add(new PatchErrorDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a boolean."));
            return;
        }

        var value = raw.GetBoolean();
        if (EqualityComparer<bool?>.Default.Equals(value, currentValue))
        {
            skipped.Add(fieldName);
            return;
        }

        setter(value);
        updated.Add(fieldName);
    }
}
