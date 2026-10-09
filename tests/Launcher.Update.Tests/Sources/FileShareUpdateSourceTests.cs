using System.Text;
using System.Text.Json;
using Launcher.Update.Manifest;
using Launcher.Update.Sources;

namespace Launcher.Update.Tests.Sources;

public sealed class FileShareUpdateSourceTests : IDisposable
{
    private readonly string _feedRoot = Path.Combine(Path.GetTempPath(), $"launcher-feed-{Guid.NewGuid():N}");
    private readonly FileShareUpdateSource _source;

    public FileShareUpdateSourceTests()
    {
        Directory.CreateDirectory(_feedRoot);
        _source = new FileShareUpdateSource(new UpdateSourceSettings(
            UpdateSourceProvider.FileShare,
            _feedRoot,
            "stable"));
    }

    [Fact]
    public async Task ReadLatestAsync_reads_an_unsigned_v2_manifest_without_a_signature_file()
    {
        var channelRoot = CreateChannelDirectory("stable");
        var manifest = CreateManifest("packages/Launcher.zip");
        await File.WriteAllBytesAsync(
            Path.Combine(channelRoot, "latest.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest));

        var latestManifest = await _source.ReadLatestAsync("stable", CancellationToken.None);

        Assert.Equal(2, latestManifest.SchemaVersion);
        Assert.Equal("1.2.3", latestManifest.Version);
        Assert.False(File.Exists(Path.Combine(channelRoot, "latest.sig")));
    }

    [Fact]
    public async Task DownloadPackageAsync_reads_the_package_relative_to_the_channel_directory()
    {
        var channelRoot = CreateChannelDirectory("stable");
        var packageBytes = Enumerable.Range(0, 257).Select(static value => (byte)value).ToArray();
        var packagePath = Path.Combine(channelRoot, "packages", "Launcher.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        await File.WriteAllBytesAsync(packagePath, packageBytes);
        var destinationPath = Path.Combine(_feedRoot, "staged.zip");
        var progress = new InlineProgress();

        await _source.DownloadPackageAsync(
            CreateManifest("packages/Launcher.zip"),
            destinationPath,
            progress,
            CancellationToken.None);

        Assert.Equal(packageBytes, await File.ReadAllBytesAsync(destinationPath));
        Assert.Equal(packageBytes.LongLength, progress.Values[^1]);
    }

    [Theory]
    [InlineData("../escape.zip")]
    [InlineData("..\\escape.zip")]
    [InlineData("C:/escape.zip")]
    [InlineData("%2e%2e/escape.zip")]
    [InlineData("/rooted.zip")]
    public async Task DownloadPackageAsync_rejects_a_package_path_that_escapes_the_channel_root(string packagePath)
    {
        var destinationPath = Path.Combine(_feedRoot, "should-not-exist.zip");

        await Assert.ThrowsAsync<ArgumentException>(() => _source.DownloadPackageAsync(
            CreateManifest(packagePath),
            destinationPath,
            null,
            CancellationToken.None));

        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task ReadLatestAsync_throws_when_a_feed_file_is_absent()
    {
        CreateChannelDirectory("stable");

        await Assert.ThrowsAnyAsync<IOException>(() => _source.ReadLatestAsync("stable", CancellationToken.None));
    }

    [Fact]
    public async Task ReadLatestAsync_rejects_a_channel_that_escapes_the_feed_root()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _source.ReadLatestAsync("../outside", CancellationToken.None));
    }

    [Fact]
    public async Task DownloadPackageAsync_honors_cancellation()
    {
        var channelRoot = CreateChannelDirectory("stable");
        var packagePath = Path.Combine(channelRoot, "package.zip");
        await File.WriteAllBytesAsync(packagePath, new byte[128]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _source.DownloadPackageAsync(
            CreateManifest("package.zip"),
            Path.Combine(_feedRoot, "staged.zip"),
            null,
            cancellation.Token));
    }

    [Fact]
    public void UpdateSourceFactory_creates_a_file_share_source_for_file_share_settings()
    {
        var source = UpdateSourceFactory.Create(new UpdateSourceSettings(
            UpdateSourceProvider.FileShare,
            _feedRoot,
            "stable"));

        Assert.IsType<FileShareUpdateSource>(source);
    }

    private string CreateChannelDirectory(string channel)
    {
        var channelRoot = Path.Combine(_feedRoot, channel);
        Directory.CreateDirectory(channelRoot);
        return channelRoot;
    }

    private static ReleaseManifest CreateManifest(string packagePath) => new()
    {
        SchemaVersion = 2,
        Channel = "stable",
        Version = "1.2.3",
        RuntimeIdentifier = "win-x64",
        PackagePath = packagePath,
        PackageBytes = 1,
        PackageSha256 = new string('a', 64)
    };

    public void Dispose()
    {
        if (Directory.Exists(_feedRoot))
        {
            Directory.Delete(_feedRoot, recursive: true);
        }
    }

    private sealed class InlineProgress : IProgress<long>
    {
        public List<long> Values { get; } = [];

        public void Report(long value) => Values.Add(value);
    }
}
