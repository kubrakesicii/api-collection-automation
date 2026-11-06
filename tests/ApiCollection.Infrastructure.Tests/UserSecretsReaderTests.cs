using ApiCollection.Infrastructure.Configuration;

namespace ApiCollection.Infrastructure.Tests;

public sealed class UserSecretsReaderTests : IDisposable
{
    private const string Id = "30c05448-c2e4-4e2a-8769-e0c3708d0c07";

    private readonly string _work = Path.Combine(Path.GetTempPath(), "ApiCollection.Secrets." + Guid.NewGuid().ToString("N"));

    public UserSecretsReaderTests() => Directory.CreateDirectory(_work);

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private string Write(string content)
    {
        var path = Path.Combine(_work, "secrets.json");
        File.WriteAllText(path, content);
        return path;
    }

    // ---- where the file lives ----

    [Fact]
    public void The_location_follows_the_dotnet_user_secrets_convention_outside_the_repository()
    {
        var windows = UserSecretsReader.PathFor(Id, appData: @"C:\Users\ada\AppData\Roaming", isWindows: true);
        var other = UserSecretsReader.PathFor(Id, home: "/home/ada", isWindows: false);

        Assert.Equal(Path.Combine(@"C:\Users\ada\AppData\Roaming", "Microsoft", "UserSecrets", Id, "secrets.json"), windows);
        Assert.Equal(Path.Combine("/home/ada", ".microsoft", "usersecrets", Id, "secrets.json"), other);
    }

    [Theory]
    [InlineData("../elsewhere")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("with space")]
    public void An_id_that_could_point_somewhere_else_is_refused(string id) =>
        Assert.Throws<ArgumentException>(() => UserSecretsReader.PathFor(id));

    // ---- reading ----

    [Fact]
    public void A_missing_file_simply_means_no_secrets()
    {
        Assert.Empty(UserSecretsReader.Read(Id, Path.Combine(_work, "does-not-exist.json")));
    }

    [Fact]
    public void Flat_colon_keys_as_written_by_dotnet_user_secrets_are_read()
    {
        var path = Write("""{ "Postman:ApiKey": "PMAK-from-file", "Postman:WorkspaceId": "00000000-aaaa-bbbb-cccc-000000000001" }""");

        var secrets = UserSecretsReader.Read(Id, path);

        Assert.Equal("PMAK-from-file", secrets["Postman:ApiKey"]);
        Assert.Equal("00000000-aaaa-bbbb-cccc-000000000001", secrets["Postman:WorkspaceId"]);
    }

    [Fact]
    public void Nested_objects_are_flattened_with_colons_and_keys_are_case_insensitive()
    {
        var path = Write("""{ "Postman": { "ApiKey": "PMAK-nested", "Retries": 3, "Debug": true }, "Other": [1, 2], "Skipped": null }""");

        var secrets = UserSecretsReader.Read(Id, path);

        Assert.Equal("PMAK-nested", secrets["postman:apikey"]);
        Assert.Equal("3", secrets["Postman:Retries"]);
        Assert.Equal("true", secrets["Postman:Debug"]);
        Assert.DoesNotContain("Other", secrets.Keys);
        Assert.DoesNotContain("Skipped", secrets.Keys);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        var path = Write("""
            {
              // my key
              "Postman:ApiKey": "PMAK-commented",
            }
            """);

        Assert.Equal("PMAK-commented", UserSecretsReader.Read(Id, path)["Postman:ApiKey"]);
    }

    [Fact]
    public void A_file_written_with_a_utf8_byte_order_mark_is_read_like_dotnet_user_secrets_writes_it()
    {
        // `dotnet user-secrets set` writes secrets.json with a BOM; a parser that chokes on it would make every real secret unreadable.
        var path = Path.Combine(_work, "bom.json");
        File.WriteAllText(path, """{ "Postman:ApiKey": "PMAK-with-bom" }""", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(0xEF, File.ReadAllBytes(path)[0]);

        Assert.Equal("PMAK-with-bom", UserSecretsReader.Read(Id, path)["Postman:ApiKey"]);
    }

    [Fact]
    public void An_empty_object_means_no_secrets()
    {
        Assert.Empty(UserSecretsReader.Read(Id, Write("{}")));
    }

    // ---- unusable files never leak their content ----

    [Fact]
    public void Broken_json_is_reported_without_quoting_the_file()
    {
        var path = Write("""{ "Postman:ApiKey": "PMAK-super-secret-value" """);

        var exception = Assert.Throws<InvalidDataException>(() => UserSecretsReader.Read(Id, path));

        Assert.Contains("not valid JSON", exception.Message);
        Assert.DoesNotContain("PMAK-super-secret-value", exception.Message);
        Assert.DoesNotContain("PMAK-super-secret-value", exception.ToString().Split("at ApiCollection")[0]);
    }

    [Theory]
    [InlineData("[\"PMAK-secret\"]")]
    [InlineData("\"PMAK-secret\"")]
    public void A_file_that_is_not_a_json_object_is_refused_without_quoting_it(string content)
    {
        var path = Write(content);

        var exception = Assert.Throws<InvalidDataException>(() => UserSecretsReader.Read(Id, path));

        Assert.Contains("must contain a JSON object", exception.Message);
        Assert.DoesNotContain("PMAK-secret", exception.Message);
    }
}
