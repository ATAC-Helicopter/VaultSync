using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VaultSync.Core.Models;

public sealed record BackupCryptoDescriptor
{
    public const int CurrentFormatVersion = 1;
    public const string PlainMetadataJson = "{}";
    private const string UnknownDescriptorValue = "unknown";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string Algorithm { get; init; } = "none";
    public string KdfProfile { get; init; } = "none";
    public string KdfParamRef { get; init; } = string.Empty;

    public static BackupCryptoDescriptor Plain() => new();

    public static BackupCryptoDescriptor Encrypted(
        string algorithm,
        string kdfProfile,
        string kdfParamRef,
        int formatVersion = CurrentFormatVersion)
    {
        return new BackupCryptoDescriptor
        {
            FormatVersion = formatVersion > 0 ? formatVersion : CurrentFormatVersion,
            Algorithm = NormalizeOrFallback(algorithm, UnknownDescriptorValue),
            KdfProfile = NormalizeOrFallback(kdfProfile, UnknownDescriptorValue),
            KdfParamRef = (kdfParamRef ?? string.Empty).Trim()
        };
    }

    public static BackupCryptoDescriptor FromMetadata(bool isEncrypted, string? descriptorJson)
    {
        if (!isEncrypted)
            return Plain();

        if (string.IsNullOrWhiteSpace(descriptorJson))
            return Encrypted(UnknownDescriptorValue, UnknownDescriptorValue, string.Empty);

        string trimmed = descriptorJson.Trim();
        if (string.Equals(trimmed, PlainMetadataJson, StringComparison.Ordinal))
            return Encrypted(UnknownDescriptorValue, UnknownDescriptorValue, string.Empty);

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Encrypted(UnknownDescriptorValue, UnknownDescriptorValue, string.Empty);

            int formatVersion = ReadInt(root, "formatVersion", CurrentFormatVersion);
            string algorithm = ReadString(root, "algorithm", UnknownDescriptorValue);
            string kdfProfile = ReadString(root, "kdfProfile", UnknownDescriptorValue);
            string kdfParamRef = ReadString(root, "kdfParamRef", string.Empty);

            // Legacy fallback keys for pre-contract payloads.
            if (string.Equals(algorithm, UnknownDescriptorValue, StringComparison.OrdinalIgnoreCase))
                algorithm = ReadString(root, "cipher", UnknownDescriptorValue);
            if (string.Equals(kdfProfile, UnknownDescriptorValue, StringComparison.OrdinalIgnoreCase))
                kdfProfile = ReadString(root, "kdf", UnknownDescriptorValue);
            if (string.IsNullOrWhiteSpace(kdfParamRef))
                kdfParamRef = ReadString(root, "paramsId", string.Empty);

            return Encrypted(algorithm, kdfProfile, kdfParamRef, formatVersion);
        }
        catch
        {
            return Encrypted(UnknownDescriptorValue, UnknownDescriptorValue, string.Empty);
        }
    }

    public string ToMetadataJson(bool isEncrypted)
    {
        if (!isEncrypted)
            return PlainMetadataJson;

        var payload = new StoragePayload
        {
            FormatVersion = FormatVersion > 0 ? FormatVersion : CurrentFormatVersion,
            Algorithm = NormalizeOrFallback(Algorithm, UnknownDescriptorValue),
            KdfProfile = NormalizeOrFallback(KdfProfile, UnknownDescriptorValue),
            KdfParamRef = (KdfParamRef ?? string.Empty).Trim()
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static int ReadInt(JsonElement root, string propertyName, int fallback)
    {
        if (!TryGetPropertyCaseInsensitive(root, propertyName, out JsonElement property))
            return fallback;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int parsed))
            return parsed;

        return fallback;
    }

    private static string ReadString(JsonElement root, string propertyName, string fallback)
    {
        if (!TryGetPropertyCaseInsensitive(root, propertyName, out JsonElement property))
            return fallback;

        if (property.ValueKind != JsonValueKind.String)
            return fallback;

        string? value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string NormalizeOrFallback(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return value.Trim();
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.TryGetProperty(propertyName, out value))
            return true;

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private sealed class StoragePayload
    {
        public int FormatVersion { get; set; }
        public string Algorithm { get; set; } = UnknownDescriptorValue;
        public string KdfProfile { get; set; } = UnknownDescriptorValue;
        public string KdfParamRef { get; set; } = string.Empty;
    }
}
