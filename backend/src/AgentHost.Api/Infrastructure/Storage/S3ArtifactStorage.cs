using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace AgentHost.Api.Infrastructure.Storage;

/// <summary>
/// Artifacts in any S3-compatible object store: AWS S3, MinIO, Ceph RGW. Selected with
/// <c>Artifacts:Provider=s3</c>; keys look like <c>artifacts/{runId}/{artifactId}-{name}</c> and are
/// what <c>artifacts.s3_path</c> stores (finally making the column's name honest).
///
/// <para><b>Download is a streamed proxy through the API, not a presigned URL.</b> A presigned URL
/// would take the bytes off the API's hot path, but it is the wrong default here: (1) the store is
/// frequently an in-cluster MinIO/Ceph that browser clients cannot route to at all, so the link would
/// simply not resolve for the very deployments this feature targets; (2) it mints a bearer capability
/// for the object that lives on past the request, outside the org-scoped check the endpoint just
/// performed, and gets copied into logs, browser history and chat; (3) the API already streams the
/// object, so the memory profile is a fixed buffer either way — what a presigned URL saves is
/// bandwidth, not memory. A deployment that wants presigned URLs also wants a public endpoint and a
/// URL-lifetime policy, which is a feature with its own configuration surface, not a default.</para>
///
/// <para>Uploads go through <see cref="TransferUtility"/>, which switches to multipart above its
/// threshold, so a large artifact is never materialized in memory either.</para>
/// </summary>
public sealed class S3ArtifactStorage : IArtifactStorage, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly S3ArtifactStorageOptions _options;
    private readonly bool _ownsClient;

    public S3ArtifactStorage(S3ArtifactStorageOptions options)
        : this(options, CreateClient(options), ownsClient: true) { }

    public S3ArtifactStorage(S3ArtifactStorageOptions options, IAmazonS3 client, bool ownsClient = false)
    {
        var errors = options.Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid S3 artifact storage configuration: " + string.Join(" ", errors));

        _options = options;
        _client = client;
        _ownsClient = ownsClient;
    }

    public string ProviderName => "s3";

    private static IAmazonS3 CreateClient(S3ArtifactStorageOptions options)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.EffectiveRegion),
        };

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            // ServiceURL and RegionEndpoint are mutually exclusive in the SDK; the endpoint wins and
            // the region survives only as the value used to sign requests.
            config.RegionEndpoint = null;
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.EffectiveRegion;
        }

        return options.HasStaticCredentials
            ? new AmazonS3Client(options.AccessKey, options.SecretKey, config)
            : new AmazonS3Client(config);
    }

    public string? DeriveKey(string runId, string artifactId, string artifactName)
        => DeriveKey(_options.KeyPrefix, runId, artifactId, artifactName);

    /// <summary>
    /// Pure key derivation, kept static so it is testable without a client or an endpoint.
    /// Returns null for any component that is empty or that contains a separator or traversal token —
    /// S3 keys are flat strings, so "../" is not magic to the store, but it would still produce a key
    /// outside the prefix that the retention sweep and any bucket policy scoped to the prefix miss.
    /// </summary>
    internal static string? DeriveKey(string keyPrefix, string runId, string artifactId, string artifactName)
    {
        if (!IsSafeComponent(runId) || !IsSafeComponent(artifactId) || !IsSafeComponent(artifactName))
            return null;

        var prefix = (keyPrefix ?? string.Empty).Trim('/');
        var suffix = $"{runId}/{artifactId}-{artifactName}";

        return prefix.Length == 0 ? suffix : $"{prefix}/{suffix}";
    }

    private static bool IsSafeComponent(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains('/')
        && !value.Contains('\\')
        && value != "."
        && !value.Contains("..", StringComparison.Ordinal);

    public async Task<long> SaveAsync(string key, Stream content, string? contentType, CancellationToken ct)
    {
        // Counted rather than trusted: IFormFile.Length is a header value, and a non-seekable stream
        // has no length at all. The size that lands in the database is the size that was written.
        await using var counting = new CountingStream(content);

        var request = new TransferUtilityUploadRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = counting,
            AutoCloseStream = false,
        };

        if (!string.IsNullOrWhiteSpace(contentType))
            request.ContentType = contentType;

        using var transfer = new TransferUtility(_client);
        await transfer.UploadAsync(request, ct);

        return counting.BytesRead;
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        try
        {
            var response = await _client.GetObjectAsync(_options.Bucket, key, ct);

            // The response's ResponseStream owns the underlying HTTP stream, so handing it to the
            // caller (who disposes it after the body is written) releases the connection.
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        try
        {
            await _client.DeleteObjectAsync(_options.Bucket, key, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone: the retention sweep is idempotent by design.
        }
    }

    public async IAsyncEnumerable<ArtifactObject> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = _options.Bucket,
            Prefix = _options.KeyPrefix.Length == 0 ? null : _options.KeyPrefix + "/",
        };

        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, ct);

            foreach (var entry in response.S3Objects)
                yield return new ArtifactObject(entry.Key, entry.LastModified.ToUniversalTime());

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated);
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    /// <summary>Pass-through stream that counts the bytes actually read out of the source.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;

        public CountingStream(Stream inner) => _inner = inner;

        public long BytesRead { get; private set; }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            BytesRead += read;
            return read;
        }

        // A multipart upload seeks back over the source; the count must follow, or a retried part
        // would be counted twice.
        public override long Seek(long offset, SeekOrigin origin)
        {
            var position = _inner.Seek(offset, origin);
            if (position < BytesRead)
                BytesRead = position;
            return position;
        }

        public override void Flush() => _inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
