using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IntellectHub.Client;

/// <summary>
/// Where an S3-compatible bucket lives and how to sign for it. Cloudflare R2 speaks
/// the S3 API, so the same credentials and the same signing cover R2, MinIO and
/// anything else with that shape.
/// </summary>
public sealed class StorageCredentials
{
    public string AccessKeyId { get; init; } = "";
    public string SecretAccessKey { get; init; } = "";
    public string Region { get; init; } = "auto";
    public string Endpoint { get; init; } = "";

    /// <summary>What is missing, in the order a person would fix it. Empty when usable.</summary>
    public string Missing()
    {
        if (string.IsNullOrWhiteSpace(Endpoint)) return "the bucket endpoint";
        if (string.IsNullOrWhiteSpace(AccessKeyId)) return "the access key id";
        if (string.IsNullOrWhiteSpace(SecretAccessKey)) return "the secret access key";
        return "";
    }
}

/// <summary>
/// Reads the storage credentials. The environment wins over secrets.json, so a
/// machine-wide setting does not have to be repeated per profile, and secrets.json
/// holds the value for the people who keep it in one place.
/// </summary>
public static class StorageConfig
{
    private const string Prefix = "r2:";

    public static StorageCredentials Load(string endpoint)
    {
        var secrets = ClientConfig.LoadSecrets();

        return new StorageCredentials
        {
            Endpoint = endpoint,
            AccessKeyId = Environment.GetEnvironmentVariable("IH_R2_ACCESS_KEY_ID")
                          ?? secrets.GetValueOrDefault(Prefix + "accessKeyId", ""),
            SecretAccessKey = Environment.GetEnvironmentVariable("IH_R2_SECRET_ACCESS_KEY")
                              ?? secrets.GetValueOrDefault(Prefix + "secretAccessKey", ""),
            Region = Environment.GetEnvironmentVariable("IH_R2_REGION")
                     ?? secrets.GetValueOrDefault(Prefix + "region", "auto"),
        };
    }
}

/// <summary>
/// A minimal S3 client: enough to put an object, take one back and remove one.
/// Requests are signed with AWS Signature Version 4, which is what R2 expects.
/// </summary>
public sealed class StorageClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly StorageCredentials _credentials;
    private readonly string _endpoint;

    public StorageClient(StorageCredentials credentials)
    {
        _credentials = credentials;
        _endpoint = credentials.Endpoint.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>Uploads an object and returns the URL it now lives at.</summary>
    public async Task<string> UploadAsync(string objectName, byte[] content, string contentType, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, ObjectUri(objectName))
        {
            Content = new ByteArrayContent(content),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        var signed = AwsV4.Sign(HttpMethod.Put, request.RequestUri!, content, _credentials, DateTimeOffset.UtcNow);
        Apply(request, signed);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"R2 rejected the upload ({(int)response.StatusCode}): {Shorten(body)}");

        return request.RequestUri!.ToString();
    }

    /// <summary>Fetches an object. Returns null when the bucket has no such key.</summary>
    public async Task<byte[]?> DownloadAsync(string objectName, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ObjectUri(objectName));
        var signed = AwsV4.Sign(HttpMethod.Get, request.RequestUri!, Array.Empty<byte>(), _credentials, DateTimeOffset.UtcNow);
        Apply(request, signed);

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"R2 rejected the download ({(int)response.StatusCode}): {Shorten(body)}");

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Removes an object. Returns false when there was nothing to remove.</summary>
    public async Task<bool> DeleteAsync(string objectName, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, ObjectUri(objectName));
        var signed = AwsV4.Sign(HttpMethod.Delete, request.RequestUri!, Array.Empty<byte>(), _credentials, DateTimeOffset.UtcNow);
        Apply(request, signed);

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"R2 rejected the delete ({(int)response.StatusCode}): {Shorten(body)}");

        return true;
    }

    /// <summary>
    /// The address of one object. The object name is percent-encoded, but the
    /// slashes are left alone: they are part of the key, not separators in a path.
    /// </summary>
    private Uri ObjectUri(string objectName)
    {
        var path = objectName.TrimStart('/');
        var encoded = new StringBuilder(path.Length);
        foreach (var c in path)
            encoded.Append(c == '/' ? '/' : Uri.EscapeDataString(c.ToString()));
        return new Uri($"{_endpoint}/{encoded}");
    }

    private static void Apply(HttpRequestMessage request, AwsV4.Signature signature)
    {
        request.Headers.TryAddWithoutValidation("x-amz-date", signature.Date);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", signature.PayloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", signature.Authorization);
    }

    private static string Shorten(string value) =>
        value.Length <= 300 ? value : value[..300] + "...";

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// AWS Signature Version 4. The signature covers the method, the path, the headers
/// that are listed as signed and a hash of the body, so a request that was changed
/// on the way no longer matches what was signed.
/// </summary>
internal static class AwsV4
{
    private const string Service = "s3";
    private const string Algorithm = "AWS4-HMAC-SHA256";

    internal sealed record Signature(string Authorization, string Date, string PayloadHash);

    public static Signature Sign(
        HttpMethod method,
        Uri uri,
        byte[] body,
        StorageCredentials credentials,
        DateTimeOffset now)
    {
        var stamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = Hex(Sha256(body));
        var host = uri.IsDefaultPort ? uri.Host : uri.Authority;

        // Only the headers the signature covers are listed, and they have to be
        // listed in the order the signature was built in.
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = stamp,
        };

        var signedHeaders = string.Join(";", headers.Keys);
        var canonicalHeaders = string.Concat(headers.Select(h => $"{h.Key}:{h.Value}\n"));
        var canonicalQuery = uri.Query.Length > 1 ? uri.Query[1..] : "";

        var canonicalRequest = string.Join("\n",
            method.Method,
            uri.AbsolutePath,
            canonicalQuery,
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var scope = $"{dateStamp}/{credentials.Region}/{Service}/aws4_request";
        var stringToSign = string.Join("\n",
            Algorithm,
            stamp,
            scope,
            Hex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signature = Hex(Sign(credentials.SecretAccessKey, dateStamp, credentials.Region, stringToSign));

        var authorization =
            $"{Algorithm} Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";

        return new Signature(authorization, stamp, payloadHash);
    }

    /// <summary>
    /// The signing key: a chain of HMACs over the date, the region, the service and
    /// the terminator. Each step narrows what the next one is allowed to sign.
    /// </summary>
    private static byte[] Sign(string secretAccessKey, string dateStamp, string region, string stringToSign)
    {
        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
        key = Hmac(key, region);
        key = Hmac(key, Service);
        key = Hmac(key, "aws4_request");
        return Hmac(key, stringToSign);
    }

    private static byte[] Hmac(byte[] key, string value) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static byte[] Sha256(byte[] value) => SHA256.HashData(value);

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
