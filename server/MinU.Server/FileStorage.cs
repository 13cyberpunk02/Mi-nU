using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace MinU.Server;

/// <summary>
/// Where uploaded files live. Clients never reach it on their own: the server checks who may see a file and hands out
/// a link that works for a few minutes, see <see cref="LinkFor"/>.
/// </summary>
public interface IFileStorage
{
    /// <summary>False when the server runs without storage; uploads are then refused.</summary>
    bool IsEnabled { get; }

    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancel);

    /// <summary>
    /// A link to the file that expires after <paramref name="lifetime"/>. Anyone holding it can download the file until
    /// then, so it goes only to someone allowed to. <paramref name="downloadName"/> makes browsers save rather than show it.
    /// </summary>
    Uri LinkFor(string key, TimeSpan lifetime, string? downloadName);

    Task DeleteAsync(string key, CancellationToken cancel);
}

/// <summary>Storage:* settings; without ServiceUrl the server runs with files off.</summary>
public sealed class StorageOptions
{
    /// <summary>How the server reaches the S3 endpoint, e.g. http://seaweedfs:8333 inside Docker.</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>The address clients download from, e.g. https://files.example.com; links are signed for it.</summary>
    public string? PublicUrl { get; set; }

    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string Bucket { get; set; } = "minu";
    public int LinkMinutes { get; set; } = 10;
}

/// <summary>Any S3-compatible storage; SeaweedFS in this project's deployment.</summary>
public sealed class S3FileStorage : IFileStorage, IDisposable
{
    readonly AmazonS3Client _client;
    // Signs links for the public address. A signature covers the host, so it has to be made for the one clients use;
    // signing is local, so this client never connects anywhere.
    readonly AmazonS3Client _signer;
    readonly string _bucket;
    readonly SemaphoreSlim _bucketReady = new(1, 1);
    bool _bucketExists;

    public S3FileStorage(StorageOptions options)
    {
        var credentials = new BasicAWSCredentials(options.AccessKey, options.SecretKey);
        _client = new AmazonS3Client(credentials, Config(options.ServiceUrl!));
        _signer = new AmazonS3Client(credentials, Config(options.PublicUrl ?? options.ServiceUrl!));
        _bucket = options.Bucket;
    }

    // Path-style addressing (host/bucket/key): SeaweedFS and a single domain need no per-bucket subdomains.
    static AmazonS3Config Config(string url) => new()
    {
        ServiceURL = url,
        ForcePathStyle = true,
        AuthenticationRegion = "us-east-1",
        // SeaweedFS doesn't take the streaming checksums newer SDKs add by default.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    };

    public bool IsEnabled => true;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancel)
    {
        await EnsureBucketAsync(cancel);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, cancel);
    }

    public Uri LinkFor(string key, TimeSpan lifetime, string? downloadName)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow + lifetime,
            Protocol = _signer.Config.ServiceURL.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP,
        };
        if (downloadName is not null)
            request.ResponseHeaderOverrides.ContentDisposition = ContentDisposition(downloadName);
        return new Uri(_signer.GetPreSignedURL(request));
    }

    public async Task DeleteAsync(string key, CancellationToken cancel) =>
        await _client.DeleteObjectAsync(_bucket, key, cancel);

    // Created on first use, so a fresh deployment needs no manual step.
    async Task EnsureBucketAsync(CancellationToken cancel)
    {
        if (_bucketExists)
            return;
        await _bucketReady.WaitAsync(cancel);
        try
        {
            if (_bucketExists)
                return;
            try
            {
                await _client.PutBucketAsync(_bucket, cancel);
            }
            catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
            {
            }
            _bucketExists = true;
        }
        finally
        {
            _bucketReady.Release();
        }
    }

    // RFC 6266: an ASCII fallback for old clients, and the real name in UTF-8.
    static string ContentDisposition(string name)
    {
        var ascii = new string(name.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_').ToArray());
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    public void Dispose()
    {
        _client.Dispose();
        _signer.Dispose();
    }
}

/// <summary>For a server without storage configured: everything else works, files are refused.</summary>
public sealed class NoFileStorage : IFileStorage
{
    public bool IsEnabled => false;
    public Task PutAsync(string key, Stream content, string contentType, CancellationToken cancel) => throw new InvalidOperationException("Storage is not set up.");
    public Uri LinkFor(string key, TimeSpan lifetime, string? downloadName) => throw new InvalidOperationException("Storage is not set up.");
    public Task DeleteAsync(string key, CancellationToken cancel) => Task.CompletedTask;
}
