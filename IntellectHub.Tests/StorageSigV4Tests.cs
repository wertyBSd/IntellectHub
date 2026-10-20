using System.Text;
using IntellectHub.Client;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// The signature the client puts on every Cloudflare R2 request. The expected
/// values were produced by the AWS SDK's own signer (botocore), so this checks
/// the implementation against the reference rather than against itself.
/// Regenerate them with IntellectHub.Tests/sigv4_reference.py.
/// </summary>
public sealed class StorageSigV4Tests
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string Scope = "20260115/auto/s3/aws4_request";
    private const string SignedHeaders = "host;x-amz-content-sha256;x-amz-date";

    private static readonly DateTimeOffset Fixed =
        new(2026, 1, 15, 12, 34, 56, TimeSpan.Zero);

    private static AwsV4.Signature Sign(string method, string url, string body = "") =>
        AwsV4.Sign(
            HttpMethod.Parse(method),
            new Uri(url),
            Encoding.UTF8.GetBytes(body),
            new StorageCredentials
            {
                AccessKeyId = AccessKey,
                SecretAccessKey = SecretKey,
                Region = "auto",
                Endpoint = "https://bucket.r2.cloudflarestorage.com",
            },
            Fixed);

    [Theory]
    // Signature=9d59afab1e709f23baa3855d11b4d71990f2aa01c67b2b67d6b51d52fbce2902
    [InlineData("GET", "https://bucket.r2.cloudflarestorage.com/folder/file.txt", "",
        "9d59afab1e709f23baa3855d11b4d71990f2aa01c67b2b67d6b51d52fbce2902")]
    // Signature=dd76b320b00d658326be48ee7355796b8efc4946242a4f11b7d8f05f22e31912
    [InlineData("PUT", "https://bucket.r2.cloudflarestorage.com/folder/file.txt", "hello from the client",
        "dd76b320b00d658326be48ee7355796b8efc4946242a4f11b7d8f05f22e31912")]
    // Signature=835406c2655d5c5974ba5582dcfcd6a4be596bb1ca25c288144e80b22c97e758
    [InlineData("DELETE", "https://bucket.r2.cloudflarestorage.com/folder/file.txt", "",
        "835406c2655d5c5974ba5582dcfcd6a4be596bb1ca25c288144e80b22c97e758")]
    // Signature=0e53ee9705604918328bf88689dda39187d8c7ef8fc02868a43058d600bf66e6
    [InlineData("GET", "https://bucket.r2.cloudflarestorage.com/folder/file.txt?versionId=42", "",
        "0e53ee9705604918328bf88689dda39187d8c7ef8fc02868a43058d600bf66e6")]
    // Signature=f27de95ceea852c90c4ae8d8d8abb19ae17daeaedee81e5b723dd486bbbac80b
    [InlineData("GET", "https://bucket.r2.cloudflarestorage.com/folder/a%20b.txt", "",
        "f27de95ceea852c90c4ae8d8d8abb19ae17daeaedee81e5b723dd486bbbac80b")]
    // Signature=05a3cd3768c37ee22910e97e50e2e4d2a4b30f535154e6f9edbe95e1399dc309
    [InlineData("GET", "https://bucket.r2.cloudflarestorage.com/a/b/c/d%20e/f.txt", "",
        "05a3cd3768c37ee22910e97e50e2e4d2a4b30f535154e6f9edbe95e1399dc309")]
    public void SignatureMatchesTheAwsSdk(string method, string url, string body, string expected)
    {
        var signature = Sign(method, url, body);

        Assert.Equal(
            $"AWS4-HMAC-SHA256 Credential={AccessKey}/{Scope}, SignedHeaders={SignedHeaders}, Signature={expected}",
            signature.Authorization);
    }

    [Fact]
    public void DateAndPayloadHashAreTheOnesTheSdkUses()
    {
        var signature = Sign("PUT", "https://bucket.r2.cloudflarestorage.com/folder/file.txt", "hello from the client");

        Assert.Equal("20260115T123456Z", signature.Date);
        Assert.Equal("244d5cb2f65778a4e36ff713d9612f0d19025ddf625c6891d270f8a4df37c67f", signature.PayloadHash);
    }

    [Fact]
    public void AnEmptyBodyHashesToTheEmptyStringHash()
    {
        var signature = Sign("GET", "https://bucket.r2.cloudflarestorage.com/folder/file.txt");

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", signature.PayloadHash);
    }

    [Fact]
    public void AChangedBodyChangesTheSignature()
    {
        var original = Sign("PUT", "https://bucket.r2.cloudflarestorage.com/f.txt", "one");
        var changed = Sign("PUT", "https://bucket.r2.cloudflarestorage.com/f.txt", "two");

        Assert.NotEqual(original.Authorization, changed.Authorization);
    }
}
