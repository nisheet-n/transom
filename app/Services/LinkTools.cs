using System.Diagnostics;
using QRCoder;

namespace Transom.Services;

public static class LinkTools
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6), MaxResponseContentBufferSize = 64_000 };

    public static Uri? Detect(string? text)
    {
        if (text is null) return null;
        var link = text.Trim().Trim('<', '>', '"', '\'', '(', ')');
        if (link.Length is < 4 or > 2048 || link.Any(char.IsWhiteSpace)) return null;
        if (link.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) link = "https://" + link;
        return Uri.TryCreate(link, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Contains('.')
            ? uri
            : null;
    }

    public static async Task<string?> ShortenAsync(Uri uri)
    {
        var link = Uri.EscapeDataString(uri.OriginalString);
        foreach (var service in new[] { "https://tinyurl.com/api-create.php?url=", "https://v.gd/create.php?format=simple&url=" })
        {
            try
            {
                var reply = (await Http.GetStringAsync(service + link)).Trim();
                if (Uri.TryCreate(reply, UriKind.Absolute, out var shortened) && shortened.Scheme == Uri.UriSchemeHttps) return reply;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
        return null;
    }

    public static byte[] QrPng(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(12);
    }
}
