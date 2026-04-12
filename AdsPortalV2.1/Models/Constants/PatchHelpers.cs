using System.Text.Json;

namespace AdsPortalV2.Models;

public static class PatchHelpers
{
    public const int MaxStringLength = 100;

    public static void UpdateString(
        JsonElement raw,
        string currentValue,
        Action<string> setter,
        string fieldName,
        ICollection<string> updated,
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
            return;
        }

        if (raw.ValueKind is not JsonValueKind.String)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a string."));
            return;
        }

        var value = raw.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be empty."));
            return;
        }

        if (value.Length > MaxStringLength)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} is too long."));
            return;
        }

        if (string.Equals(value, currentValue, StringComparison.Ordinal))
        {
            skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
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
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors)
    {
        if (value == currentValue)
        {
            skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
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
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors,
        bool allowNull = true)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            if (!allowNull)
            {
                errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
                return;
            }

            if (currentValue is null)
            {
                skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
                return;
            }

            setter(null);
            updated.Add(fieldName);
            return;
        }

        if (raw.ValueKind is not JsonValueKind.String)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a string."));
            return;
        }

        var value = raw.GetString()?.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            if (currentValue is null)
            {
                skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
                return;
            }

            setter(null);
            updated.Add(fieldName);
            return;
        }

        if (value.Length > MaxStringLength)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} is too long."));
            return;
        }

        if (string.Equals(value, currentValue, StringComparison.Ordinal))
        {
            skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
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
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
            return;
        }

        if (!raw.TryGetInt32(out var value))
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be an integer."));
            return;
        }

        if (value == currentValue)
        {
            skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
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
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors,
        bool allowNull = true)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            if (!allowNull)
            {
                errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
                return;
            }

            if (currentValue is null)
            {
                skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
                return;
            }

            setter(null);
            updated.Add(fieldName);
            return;
        }

        if (!raw.TryGetInt32(out var value))
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be an integer."));
            return;
        }

        if (EqualityComparer<int?>.Default.Equals(value, currentValue))
        {
            skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
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
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
            return;
        }

        if (!raw.TryGetDecimal(out var value))
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a decimal."));
            return;
        }

        if (value == currentValue)
        {
            skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
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
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors,
        bool allowNull = true)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            if (!allowNull)
            {
                errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
                return;
            }

            if (currentValue is null)
            {
                skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
                return;
            }

            setter(null);
            updated.Add(fieldName);
            return;
        }

        if (!raw.TryGetDecimal(out var value))
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a decimal."));
            return;
        }

        if (EqualityComparer<decimal?>.Default.Equals(value, currentValue))
        {
            skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
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
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
            return;
        }

        if (raw.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a boolean."));
            return;
        }

        var value = raw.GetBoolean();
        if (value == currentValue)
        {
            skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
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
        ICollection<PatchIssueDto> skipped,
        ICollection<PatchIssueDto> errors,
        bool allowNull = true)
    {
        if (raw.ValueKind is JsonValueKind.Undefined)
            return;

        if (raw.ValueKind is JsonValueKind.Null)
        {
            if (!allowNull)
            {
                errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} cannot be null."));
                return;
            }

            if (currentValue is null)
            {
                skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
                return;
            }

            setter(null);
            updated.Add(fieldName);
            return;
        }

        if (raw.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            errors.Add(new PatchIssueDto(PatchErrorCodes.InvalidValue, fieldName, $"{fieldName} must be a boolean."));
            return;
        }

        var value = raw.GetBoolean();
        if (EqualityComparer<bool?>.Default.Equals(value, currentValue))
        {
            skipped.Add(new PatchIssueDto(PatchErrorCodes.Skipped, fieldName, $"{fieldName} not changed."));
            return;
        }

        setter(value);
        updated.Add(fieldName);
    }
}
