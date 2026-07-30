using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed class BackgroundWebResearchService
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private const int MaximumCharacters = 120_000;
    private const int MaximumRedirects = 4;
    private static readonly Regex ScriptPattern = new(
        @"<(script|style|noscript|svg)\b[^>]*>.*?</\1>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagPattern = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex SpacePattern = new(
        @"[ \t\f\v]+",
        RegexOptions.Compiled);
    private static readonly Regex BlankLinePattern = new(
        @"\n{3,}",
        RegexOptions.Compiled);
    private readonly HttpClient _httpClient;

    public BackgroundWebResearchService(HttpClient? httpClient = null)
    {
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            return;
        }
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip
                                     | DecompressionMethods.Deflate
                                     | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(12)
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(35)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "NOVA-Background-Research/1.0");
    }

    public async Task<string> FetchPublicPageAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var current = ParsePublicHttpsUri(address);
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            await EnsurePublicHostAsync(current, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("text/plain", .9));
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json", .7));
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                if (redirect == MaximumRedirects
                    || response.Headers.Location is null)
                {
                    throw new InvalidOperationException(
                        "Background research exceeded the safe redirect limit.");
                }
                current = ParsePublicHttpsUri(
                    response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location.ToString()
                        : new Uri(current, response.Headers.Location).ToString());
                continue;
            }

            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType
                            ?? "application/octet-stream";
            if (!IsReadableMediaType(mediaType))
            {
                throw new InvalidOperationException(
                    $"Background research only reads text pages; '{mediaType}' was refused.");
            }
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength > MaximumBytes)
            {
                throw new InvalidOperationException(
                    "Background research page exceeds the 2 MB safety limit.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            var bytes = await ReadBoundedAsync(stream, cancellationToken);
            var charset = response.Content.Headers.ContentType?.CharSet;
            var text = Decode(bytes, charset);
            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                text = ExtractReadableText(text);
            }
            text = text.Length <= MaximumCharacters
                ? text
                : text[..MaximumCharacters] + "\n… content truncated by NOVA …";
            return JsonSerializer.Serialize(new
            {
                mode = "background",
                opened_browser = false,
                url = current.ToString(),
                content_type = mediaType,
                bytes = bytes.Length,
                text
            });
        }

        throw new InvalidOperationException("Background research did not produce a page.");
    }

    public static Uri ParsePublicHttpsUri(string address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "Background research requires a public HTTPS URL without embedded credentials.");
        }
        if (uri.IsLoopback
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Background research cannot access localhost or private services.");
        }
        if (IPAddress.TryParse(uri.Host, out var addressValue)
            && !IsPublicAddress(addressValue))
        {
            throw new InvalidOperationException(
                "Background research cannot access private or link-local addresses.");
        }
        return uri;
    }

    private static async Task EnsurePublicHostAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        }
        catch (SocketException exception)
        {
            throw new InvalidOperationException(
                $"Unable to resolve background research host '{uri.Host}'.",
                exception);
        }
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new InvalidOperationException(
                "Background research refused a host that resolves to a private or reserved address.");
        }
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return false;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var ipv6 = address.GetAddressBytes();
            return (ipv6[0] & 0xFE) != 0xFC
                   && !(ipv6[0] == 0x20
                        && ipv6[1] == 0x01
                        && ipv6[2] == 0x0D
                        && ipv6[3] == 0xB8);
        }
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            0 or 10 or 127 => false,
            100 when bytes[1] is >= 64 and <= 127 => false,
            169 when bytes[1] == 254 => false,
            172 when bytes[1] is >= 16 and <= 31 => false,
            192 when bytes[1] == 168 => false,
            192 when bytes[1] == 0 => false,
            192 when bytes[1] == 88 && bytes[2] == 99 => false,
            198 when bytes[1] is 18 or 19 => false,
            198 when bytes[1] == 51 && bytes[2] == 100 => false,
            203 when bytes[1] == 0 && bytes[2] == 113 => false,
            >= 224 => false,
            _ => true
        };
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }
            if (buffer.Length + read > MaximumBytes)
            {
                throw new InvalidOperationException(
                    "Background research page exceeds the 2 MB safety limit.");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(charset)
                ? Encoding.GetEncoding(charset).GetString(bytes)
                : Encoding.UTF8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private static string ExtractReadableText(string html)
    {
        var text = ScriptPattern.Replace(html, "\n");
        text = TagPattern.Replace(text, "\n");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        text = string.Join(
            "\n",
            text.Split('\n')
                .Select(line => SpacePattern.Replace(line, " ").Trim())
                .Where(line => line.Length > 0));
        return BlankLinePattern.Replace(text, "\n\n");
    }

    private static bool IsReadableMediaType(string mediaType)
        => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
           || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
           || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}
