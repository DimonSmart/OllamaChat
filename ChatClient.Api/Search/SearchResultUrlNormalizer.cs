using System.Text;

namespace ChatClient.Api.Search;

internal static class SearchResultUrlNormalizer
{
    public static string? NormalizeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        var normalized = value.Trim();
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return normalized;

        return TryDecodeBraveImageProxyUrl(uri, out var decodedUrl)
            ? decodedUrl
            : normalized;
    }

    private static bool TryDecodeBraveImageProxyUrl(Uri uri, out string decodedUrl)
    {
        decodedUrl = string.Empty;
        if (!uri.Host.Equals("imgs.search.brave.com", StringComparison.OrdinalIgnoreCase))
            return false;

        const string marker = "/g:ce/";
        var markerIndex = uri.AbsolutePath.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        var encodedCandidate = uri.AbsolutePath[(markerIndex + marker.Length)..];
        if (string.IsNullOrWhiteSpace(encodedCandidate))
            return false;

        // Brave stores the payload across path segments, so separator slashes are not part of the base64url data.
        var base64 = encodedCandidate
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .Replace('-', '+')
            .Replace('_', '/');

        var padding = base64.Length % 4;
        if (padding is > 0)
            base64 = base64.PadRight(base64.Length + (4 - padding), '=');

        try
        {
            var bytes = Convert.FromBase64String(base64);
            var candidate = Encoding.UTF8.GetString(bytes);
            if (!TryCreateHttpUri(candidate, out var decodedUri))
                return false;

            decodedUrl = decodedUri.ToString();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryCreateHttpUri(string? value, out Uri targetUri)
    {
        targetUri = default!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsedUri))
            return false;

        if (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps)
            return false;

        targetUri = parsedUri;
        return true;
    }
}
