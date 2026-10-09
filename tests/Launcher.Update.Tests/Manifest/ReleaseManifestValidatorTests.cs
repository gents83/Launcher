using System.Text;
using System.Text.Json;
using Launcher.Update.Manifest;

namespace Launcher.Update.Tests.Manifest;

public sealed class ReleaseManifestValidatorTests
{
    private const string ExpectedChannel = "stable";
    private const string ExpectedRid = "win-x64";
    private const string ActiveVersion = "1.2.2";
    private static readonly string ValidSha256 = new('a', 64);

    [Fact]
    public void ValidateMetadata_accepts_unsigned_schema_v2_without_a_key_id()
    {
        var manifest = CreateManifest(schemaVersion: 2);

        var validated = ReleaseManifestValidator.ValidateMetadata(manifest, ExpectedChannel, ExpectedRid);

        Assert.Same(manifest, validated);
        Assert.Equal(2, validated.SchemaVersion);
    }

    [Fact]
    public void Validate_returns_a_valid_stable_manifest()
    {
        var manifest = CreateManifest();

        var validated = ReleaseManifestValidator.Validate(
            manifest,
            ExpectedChannel,
            ExpectedRid,
            ActiveVersion);

        Assert.Same(manifest, validated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(-1)]
    public void Validate_rejects_unknown_schema_versions(int schemaVersion)
    {
        AssertInvalid(CreateManifest(schemaVersion: schemaVersion));
    }

    [Fact]
    public void Validate_rejects_a_different_channel()
    {
        AssertInvalid(CreateManifest(channel: "preview"));
    }

    [Fact]
    public void Validate_rejects_a_different_runtime_identifier()
    {
        AssertInvalid(CreateManifest(runtimeIdentifier: "win-arm64"));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-alpha..1")]
    [InlineData("1.2.3-alpha beta")]
    public void Validate_rejects_non_semver_versions(string version)
    {
        AssertInvalid(CreateManifest(version: version));
    }

    [Theory]
    [InlineData("SchemaVersion")]
    [InlineData("Channel")]
    [InlineData("Version")]
    [InlineData("RuntimeIdentifier")]
    [InlineData("PackagePath")]
    [InlineData("PackageBytes")]
    [InlineData("PackageSha256")]
    public void Validate_rejects_json_with_a_missing_field(string omittedProperty)
    {
        var properties = new Dictionary<string, object>
        {
            ["SchemaVersion"] = 2,
            ["Channel"] = ExpectedChannel,
            ["Version"] = "1.2.3",
            ["RuntimeIdentifier"] = ExpectedRid,
            ["PackagePath"] = "packages/Launcher-1.2.3-win-x64.zip",
            ["PackageBytes"] = 123456L,
            ["PackageSha256"] = ValidSha256
        };
        properties.Remove(omittedProperty);

        var manifest = ReleaseManifest.Parse(JsonSerializer.SerializeToUtf8Bytes(properties));

        Assert.Throws<UpdateManifestException>(() => ReleaseManifestValidator.Validate(
            manifest,
            ExpectedChannel,
            ExpectedRid,
            ActiveVersion));
    }

    [Fact]
    public void Parse_rejects_property_names_that_do_not_match_exactly()
    {
        const string json = """
            {
              "SchemaVersion": 2,
              "channel": "stable",
              "Version": "1.2.3",
              "RuntimeIdentifier": "win-x64",
              "PackagePath": "packages/Launcher-1.2.3-win-x64.zip",
              "PackageBytes": 123456,
              "PackageSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            }
            """;

        Assert.Throws<UpdateManifestException>(() => ReleaseManifestValidator.Validate(
            ReleaseManifest.Parse(Encoding.UTF8.GetBytes(json)),
            ExpectedChannel,
            ExpectedRid,
            ActiveVersion));
    }

    [Fact]
    public void Parse_rejects_unknown_json_fields()
    {
        var knownJson = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(CreateManifest()));
        var jsonWithUnknownField = knownJson[..^1] + ",\"UnexpectedField\":\"value\"}";

        Assert.Throws<UpdateManifestException>(() => ReleaseManifest.Parse(
            Encoding.UTF8.GetBytes(jsonWithUnknownField)));
    }

    [Fact]
    public void Parse_rejects_the_removed_key_id_property()
    {
        var validManifestJson = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(CreateManifest(schemaVersion: 2)));
        var manifestWithKeyId = validManifestJson[..^1] + ",\"KeyId\":\"launcher-release\"}";

        Assert.Throws<UpdateManifestException>(() => ReleaseManifest.Parse(
            Encoding.UTF8.GetBytes(manifestWithKeyId)));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    public void Validate_rejects_a_sha256_with_the_wrong_length(int length)
    {
        AssertInvalid(CreateManifest(packageSha256: new string('a', length)));
    }

    [Fact]
    public void Validate_rejects_a_sha256_that_is_not_hexadecimal()
    {
        AssertInvalid(CreateManifest(packageSha256: new string('g', 64)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_rejects_nonpositive_package_sizes(long packageBytes)
    {
        AssertInvalid(CreateManifest(packageBytes: packageBytes));
    }

    [Theory]
    [InlineData("/packages/Launcher.zip")]
    [InlineData("C:/packages/Launcher.zip")]
    [InlineData("\\\\server\\share\\Launcher.zip")]
    [InlineData("\\packages\\Launcher.zip")]
    public void Validate_rejects_rooted_package_paths(string packagePath)
    {
        AssertInvalid(CreateManifest(packagePath: packagePath));
    }

    [Theory]
    [InlineData("packages/../Launcher.zip")]
    [InlineData("./packages/Launcher.zip")]
    [InlineData("packages/./Launcher.zip")]
    [InlineData("packages//Launcher.zip")]
    [InlineData("packages/Launcher.zip/")]
    public void Validate_rejects_empty_or_dot_path_components(string packagePath)
    {
        AssertInvalid(CreateManifest(packagePath: packagePath));
    }

    [Theory]
    [InlineData("https://example.invalid/Launcher.zip")]
    [InlineData("file:///packages/Launcher.zip")]
    [InlineData("https:Launcher.zip")]
    [InlineData("packages/Launcher.zip?download=1")]
    [InlineData("packages/Launcher.zip#fragment")]
    public void Validate_rejects_uri_like_package_paths(string packagePath)
    {
        AssertInvalid(CreateManifest(packagePath: packagePath));
    }

    [Theory]
    [InlineData("packages/%2e%2e/preview/latest.zip")]
    [InlineData("packages/%2fpreview/latest.zip")]
    [InlineData("packages/%5cpreview/latest.zip")]
    [InlineData("packages/%252e%252e/preview/latest.zip")]
    public void Validate_rejects_percent_encoded_package_paths(string packagePath)
    {
        AssertInvalid(CreateManifest(packagePath: packagePath));
    }

    [Theory]
    [InlineData("1.2.1", "1.2.2")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+different-build", "1.2.3+active-build")]
    public void Validate_rejects_versions_not_newer_than_the_active_version(
        string version,
        string activeVersion)
    {
        Assert.Throws<UpdateManifestException>(() => ReleaseManifestValidator.Validate(
            CreateManifest(version: version),
            ExpectedChannel,
            ExpectedRid,
            activeVersion));
    }

    [Fact]
    public void Validate_uses_semver_prerelease_precedence()
    {
        var manifest = CreateManifest(version: "1.2.3-rc.2");

        var validated = ReleaseManifestValidator.Validate(
            manifest,
            ExpectedChannel,
            ExpectedRid,
            "1.2.3-rc.1");

        Assert.Same(manifest, validated);
    }

    [Theory]
    [InlineData("1.2.3-beta", "1.2.3-Beta")]
    [InlineData("1.2.3-rc.10", "1.2.3-rc.2")]
    public void Validate_compares_prerelease_identifiers_using_semver_precedence(
        string version,
        string activeVersion)
    {
        var manifest = CreateManifest(version: version);

        var validated = ReleaseManifestValidator.Validate(
            manifest,
            ExpectedChannel,
            ExpectedRid,
            activeVersion);

        Assert.Same(manifest, validated);
    }

    [Fact]
    public void Validate_rejects_an_invalid_active_semver()
    {
        Assert.Throws<UpdateManifestException>(() => ReleaseManifestValidator.Validate(
            CreateManifest(),
            ExpectedChannel,
            ExpectedRid,
            "1.2"));
    }

    [Fact]
    public void Parse_accepts_a_manifest_at_the_64_kib_limit()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(CreateManifest());
        var manifestBytes = new byte[64 * 1024];
        json.CopyTo(manifestBytes, 0);
        Array.Fill(manifestBytes, (byte)' ', json.Length, manifestBytes.Length - json.Length);

        var manifest = ReleaseManifest.Parse(manifestBytes);

        Assert.Equal(2, manifest.SchemaVersion);
    }

    [Fact]
    public void Parse_rejects_a_manifest_over_the_64_kib_limit()
    {
        var manifestBytes = new byte[(64 * 1024) + 1];

        Assert.Throws<UpdateManifestException>(() => ReleaseManifest.Parse(manifestBytes));
    }

    private static void AssertInvalid(ReleaseManifest manifest)
    {
        Assert.Throws<UpdateManifestException>(() => ReleaseManifestValidator.Validate(
            manifest,
            ExpectedChannel,
            ExpectedRid,
            ActiveVersion));
    }

    private static ReleaseManifest CreateManifest(
        int schemaVersion = 2,
        string channel = ExpectedChannel,
        string version = "1.2.3",
        string runtimeIdentifier = ExpectedRid,
        string packagePath = "packages/Launcher-1.2.3-win-x64.zip",
        long packageBytes = 123456,
        string packageSha256 = null!) => new()
        {
            SchemaVersion = schemaVersion,
            Channel = channel,
            Version = version,
            RuntimeIdentifier = runtimeIdentifier,
            PackagePath = packagePath,
            PackageBytes = packageBytes,
            PackageSha256 = packageSha256 ?? ValidSha256
        };
}
