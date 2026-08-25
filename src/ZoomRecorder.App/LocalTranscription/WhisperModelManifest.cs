using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZoomRecorder.App.LocalTranscription;

internal sealed record WhisperModelManifest(
    string FileName,
    Uri DownloadUri,
    long ByteLength,
    string Sha256)
{
    private const int SupportedSchemaVersion = 1;
    private const string RepositoryPathPrefix = "/ggerganov/whisper.cpp/resolve/";

    public static WhisperModelManifest Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The model manifest must be a JSON object.");
            }

            var schemaVersion = ReadInt64(root, "schemaVersion");
            if (schemaVersion != SupportedSchemaVersion)
            {
                throw Invalid($"Unsupported model manifest schema version '{schemaVersion}'.");
            }

            var fileName = ReadString(root, "fileName");
            ValidateFileName(fileName);

            var uriText = ReadString(root, "downloadUri");
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps ||
                !downloadUri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) ||
                !downloadUri.IsDefaultPort ||
                !string.IsNullOrEmpty(downloadUri.Query) ||
                !string.IsNullOrEmpty(downloadUri.Fragment) ||
                !HasExpectedRepositoryPath(downloadUri, fileName))
            {
                throw Invalid("The model manifest download URI is not the pinned Hugging Face repository path.");
            }

            var byteLength = ReadInt64(root, "byteLength");
            if (byteLength <= 0)
            {
                throw Invalid("The model manifest byte length must be positive.");
            }

            var sha256 = ReadString(root, "sha256");
            if (!Regex.IsMatch(sha256, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            {
                throw Invalid("The model manifest SHA-256 must be 64 lowercase hexadecimal characters.");
            }

            return new WhisperModelManifest(fileName, downloadUri, byteLength, sha256);
        }
        catch (JsonException exception)
        {
            throw Invalid("The model manifest is not valid JSON.", exception);
        }
    }

    private static long ReadInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || !property.TryGetInt64(out var value))
        {
            throw Invalid($"The model manifest property '{propertyName}' must be an integer.");
        }

        return value;
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw Invalid($"The model manifest property '{propertyName}' must be a nonempty string.");
        }

        return property.GetString()!;
    }

    private static bool HasExpectedRepositoryPath(Uri downloadUri, string fileName)
    {
        if (!downloadUri.AbsolutePath.StartsWith(RepositoryPathPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var segments = downloadUri.AbsolutePath[RepositoryPathPrefix.Length..].Split('/', StringSplitOptions.None);
        return segments.Length == 2 &&
            !string.IsNullOrWhiteSpace(segments[0]) &&
            segments[1].Equals(Uri.EscapeDataString(fileName), StringComparison.Ordinal);
    }

    private static void ValidateFileName(string fileName)
    {
        if (Path.GetFileName(fileName) != fileName ||
            fileName is "." or ".." ||
            !Regex.IsMatch(fileName, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant))
        {
            throw Invalid("The model manifest file name is invalid.");
        }
    }

    private static InvalidDataException Invalid(string message, Exception? innerException = null) =>
        new(message, innerException);
}
