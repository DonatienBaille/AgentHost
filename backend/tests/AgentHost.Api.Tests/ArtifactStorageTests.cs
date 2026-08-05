using System.Text;
using AgentHost.Api.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// Local artifact storage end to end (it needs nothing but a temp directory), plus the pure parts of
/// the S3 backend: key derivation and configuration validation.
///
/// The S3 implementation's I/O is NOT covered — there is no reachable object store in this
/// environment, and a test with a fake HTTP endpoint would assert the AWS SDK's behaviour, not ours.
/// </summary>
public class ArtifactStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenthost-artifacts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private LocalArtifactStorage Storage() => new(_root);

    [Fact]
    public void LocalKey_IsTheAbsolutePathTheEndpointUsedToWriteDirectly()
    {
        var key = Storage().DeriveKey("01RUN", "01ART", "report.txt");

        Assert.Equal(Path.Combine(_root, "01RUN", "01ART-report.txt"), key);
    }

    [Theory]
    [InlineData("../../etc", "01ART", "passwd")]
    [InlineData("01RUN", "01ART", "../../../etc/passwd")]
    [InlineData("", "01ART", "report.txt")]
    [InlineData("01RUN", "", "report.txt")]
    [InlineData("01RUN", "01ART", "")]
    public void LocalKey_RefusesAnythingThatWouldLeaveTheStorageRoot(string runId, string artifactId, string name)
    {
        Assert.Null(Storage().DeriveKey(runId, artifactId, name));
    }

    [Fact]
    public async Task LocalStorage_RoundTripsBytesAndReportsTheSizeItActuallyWrote()
    {
        var storage = Storage();
        var payload = Encoding.UTF8.GetBytes("artifact payload ☃\n");
        var key = storage.DeriveKey("01RUN", "01ART", "report.txt")!;

        var written = await storage.SaveAsync(key, new MemoryStream(payload), "text/plain", default);
        Assert.Equal(payload.Length, written);

        await using var read = await storage.OpenReadAsync(key, default);
        Assert.NotNull(read);
        using var buffer = new MemoryStream();
        await read!.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task LocalStorage_RefusesToServeAKeyOutsideItsRoot()
    {
        // A row written under a different Artifacts:StoragePath (or tampered with) must not turn the
        // download endpoint into an arbitrary-file reader.
        var storage = Storage();
        var outside = Path.Combine(Path.GetTempPath(), "agenthost-outside-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(outside, "secret");

        try
        {
            Assert.Null(await storage.OpenReadAsync(outside, default));
            await Assert.ThrowsAsync<ArgumentException>(() => storage.SaveAsync(outside, new MemoryStream([1]), null, default));
            await storage.DeleteAsync(outside, default);
            Assert.True(File.Exists(outside)); // delete was a no-op, not a deletion outside the root
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task LocalStorage_MissingObjectReadsAsNullAndDeletesQuietly()
    {
        var storage = Storage();
        var key = storage.DeriveKey("01RUN", "01ART", "nope.txt")!;

        Assert.Null(await storage.OpenReadAsync(key, default));
        await storage.DeleteAsync(key, default); // must not throw
    }

    [Fact]
    public async Task LocalStorage_ListAndDelete_DriveTheRetentionSweep()
    {
        var storage = Storage();
        var first = storage.DeriveKey("01RUN", "01ART", "a.txt")!;
        var second = storage.DeriveKey("02RUN", "02ART", "b.txt")!;
        await storage.SaveAsync(first, new MemoryStream([1, 2, 3]), null, default);
        await storage.SaveAsync(second, new MemoryStream([4]), null, default);

        var listed = new List<ArtifactObject>();
        await foreach (var entry in storage.ListAsync(default))
            listed.Add(entry);

        Assert.Equal(2, listed.Count);
        Assert.Contains(listed, e => e.Key == first);
        Assert.All(listed, e => Assert.True(e.LastModifiedUtc > DateTime.UtcNow.AddMinutes(-5)));

        await storage.DeleteAsync(first, default);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task LocalStorage_ListOnAnEmptyOrMissingRoot_YieldsNothing()
    {
        var listed = 0;
        await foreach (var _ in Storage().ListAsync(default))
            listed++;

        Assert.Equal(0, listed);
    }

    // ---- S3: pure logic only (no network) ----

    [Fact]
    public void S3Key_IsPrefixedAndMeaningful()
    {
        Assert.Equal("artifacts/01RUN/01ART-report.txt", S3ArtifactStorage.DeriveKey("artifacts", "01RUN", "01ART", "report.txt"));
        Assert.Equal("prod/runs/01RUN/01ART-report.txt", S3ArtifactStorage.DeriveKey("prod/runs/", "01RUN", "01ART", "report.txt"));
        Assert.Equal("01RUN/01ART-report.txt", S3ArtifactStorage.DeriveKey("", "01RUN", "01ART", "report.txt"));
    }

    [Theory]
    [InlineData("a/b", "01ART", "report.txt")]
    [InlineData("01RUN", "01ART", "../escape.txt")]
    [InlineData("01RUN", "01ART", @"a\b")]
    [InlineData("01RUN", "..", "report.txt")]
    [InlineData("01RUN", "01ART", " ")]
    public void S3Key_RefusesComponentsThatWouldLeaveThePrefix(string runId, string artifactId, string name)
    {
        // S3 keys are flat strings, so "../" is not traversal to the store — but it would still put
        // the object outside the prefix the retention sweep and any bucket policy are scoped to.
        Assert.Null(S3ArtifactStorage.DeriveKey("artifacts", runId, artifactId, name));
    }

    [Fact]
    public void S3Options_ReadTheConfigurationSection_AndDefaultSensibly()
    {
        var options = S3ArtifactStorageOptions.FromConfiguration(Config(new()
        {
            ["Artifacts:S3:Bucket"] = "agenthost-artifacts",
            ["Artifacts:S3:ServiceUrl"] = "http://minio:9000",
        }));

        Assert.Equal("agenthost-artifacts", options.Bucket);
        Assert.True(options.ForcePathStyle); // MinIO/Ceph have no per-bucket DNS
        Assert.Equal("artifacts", options.KeyPrefix);
        Assert.Equal("us-east-1", options.EffectiveRegion);
        Assert.False(options.HasStaticCredentials);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void S3Options_AgainstRealAws_DoNotForcePathStyle()
    {
        var options = S3ArtifactStorageOptions.FromConfiguration(Config(new()
        {
            ["Artifacts:S3:Bucket"] = "b",
            ["Artifacts:S3:Region"] = "eu-west-3",
        }));

        Assert.False(options.ForcePathStyle);
        Assert.Equal("eu-west-3", options.EffectiveRegion);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void S3Options_MissingBucketOrRegion_IsAConfigurationError()
    {
        var noBucket = S3ArtifactStorageOptions.FromConfiguration(Config(new() { ["Artifacts:S3:Region"] = "eu-west-3" }));
        Assert.Contains(noBucket.Validate(), e => e.Contains("Bucket"));

        var noRegion = S3ArtifactStorageOptions.FromConfiguration(Config(new() { ["Artifacts:S3:Bucket"] = "b" }));
        Assert.Contains(noRegion.Validate(), e => e.Contains("Region"));
    }

    [Fact]
    public void S3Options_HalfACredentialPair_IsAConfigurationError()
    {
        var options = S3ArtifactStorageOptions.FromConfiguration(Config(new()
        {
            ["Artifacts:S3:Bucket"] = "b",
            ["Artifacts:S3:Region"] = "eu-west-3",
            ["Artifacts:S3:AccessKey"] = "AKIA...",
        }));

        // Silently falling back to the ambient chain here surfaces as a puzzling 403 at upload time.
        Assert.Contains(options.Validate(), e => e.Contains("AccessKey"));
    }

    [Fact]
    public void S3Options_MalformedServiceUrl_IsAConfigurationError()
    {
        var options = S3ArtifactStorageOptions.FromConfiguration(Config(new()
        {
            ["Artifacts:S3:Bucket"] = "b",
            ["Artifacts:S3:ServiceUrl"] = "minio:9000",
        }));

        Assert.Contains(options.Validate(), e => e.Contains("ServiceUrl"));
    }

    [Fact]
    public void S3Storage_RefusesToConstructWithAnInvalidConfiguration()
    {
        var options = new S3ArtifactStorageOptions { Bucket = "" };

        Assert.Throws<InvalidOperationException>(() => new S3ArtifactStorage(options));
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
