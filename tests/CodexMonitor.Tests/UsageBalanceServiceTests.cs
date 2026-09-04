using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexMonitor.Tests;

public sealed class UsageBalanceServiceTests
{
    [Fact]
    public async Task ReadAsync_RejectsResponseBodyLargerThanConfiguredLimit()
    {
        using var fixture = new BalanceFixture(
            new ByteArrayContent(new byte[33]),
            maximumResponseBytes: 32);

        BalanceSnapshot snapshot = await fixture.Service.ReadAsync();

        Assert.True(snapshot.IsConfigured);
        Assert.Contains("响应体超过 32 字节上限", snapshot.Error);
    }

    [Fact]
    public async Task ReadAsync_CancelsResponseBodyThatStopsProducingData()
    {
        using var fixture = new BalanceFixture(
            new StreamContent(new NeverEndingReadStream()),
            responseBodyTimeout: TimeSpan.FromMilliseconds(50));

        BalanceSnapshot snapshot = await fixture.Service.ReadAsync();

        Assert.True(snapshot.IsConfigured);
        Assert.Equal("余额查询超时", snapshot.Error);
    }

    [Fact]
    public async Task ReadAsync_ReportsOfficialOAuthAsUnavailable()
    {
        string settingsConfig = JsonSerializer.Serialize(
            new
            {
                auth = new
                {
                    auth_mode = "chatgpt",
                    OPENAI_API_KEY = (string?)null,
                    tokens = new { access_token = "oauth-access-token" },
                },
                config = string.Empty,
            });
        using var fixture = new BalanceFixture(
            new StringContent("{}"),
            providerName: "OpenAI Official",
            metadata: "{}",
            settingsConfig: settingsConfig);

        BalanceSnapshot snapshot = await fixture.Service.ReadAsync();

        Assert.True(snapshot.IsConfigured);
        Assert.Equal("OpenAI Official", snapshot.ProviderName);
        Assert.Equal("官方 OAuth 未提供余额查询", snapshot.Error);
        Assert.Null(fixture.LastRequestUri);
    }

    [Fact]
    public async Task ReadAsync_UsesBaseUrlFromCurrentTomlModelProvider()
    {
        string metadata = JsonSerializer.Serialize(
            new
            {
                usage_script = new
                {
                    enabled = true,
                    code = "request.url = '{{baseUrl}}/v1/usage'",
                },
            });
        string configuration =
            """
            model_provider = "selected"

            [model_providers.decoy]
            base_url = "https://wrong.example"

            [model_providers.selected]
            base_url = "https://provider.example/root/"
            """;
        string settingsConfig = JsonSerializer.Serialize(
            new
            {
                auth = new { OPENAI_API_KEY = "test-key" },
                config = configuration,
            });
        using var fixture = new BalanceFixture(
            new StringContent("{\"remaining\": 12.5, \"unit\": \"USD\"}"),
            metadata: metadata,
            settingsConfig: settingsConfig);

        BalanceSnapshot snapshot = await fixture.Service.ReadAsync();

        Assert.Null(snapshot.Error);
        Assert.Equal(12.5m, snapshot.Remaining);
        Assert.Equal(
            new Uri("https://provider.example/root/v1/usage"),
            fixture.LastRequestUri);
    }

    private sealed class BalanceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"CodexMonitor.BalanceTests-{Guid.NewGuid():N}");

        private readonly HttpClient _httpClient;

        public BalanceFixture(
            HttpContent responseContent,
            TimeSpan? responseBodyTimeout = null,
            int maximumResponseBytes = 1024,
            string providerName = "Test provider",
            string? metadata = null,
            string? settingsConfig = null)
        {
            Directory.CreateDirectory(_root);
            string databasePath = Path.Combine(_root, "cc-switch.db");
            CreateDatabase(
                databasePath,
                providerName,
                metadata ?? "{\"usage_script\":{\"enabled\":\"true\",\"baseUrl\":\"https://example.test\",\"code\":\"{{baseUrl}}/v1/usage\"}}",
                settingsConfig ?? "{\"auth\":{\"OPENAI_API_KEY\":\"test-key\"}}");

            var handler = new StaticResponseHandler(responseContent);
            _httpClient = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            Handler = handler;

            Service = new UsageBalanceService(
                _httpClient,
                databasePath,
                databaseTimeout: TimeSpan.FromSeconds(1),
                responseHeadersTimeout: TimeSpan.FromSeconds(1),
                responseBodyTimeout: responseBodyTimeout ?? TimeSpan.FromSeconds(1),
                maximumResponseBytes);
        }

        public UsageBalanceService Service { get; }

        public Uri? LastRequestUri => Handler.LastRequestUri;

        private StaticResponseHandler Handler { get; }

        public void Dispose()
        {
            Service.Dispose();
            _httpClient.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }

        private static void CreateDatabase(
            string databasePath,
            string providerName,
            string metadata,
            string settingsConfig)
        {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE providers (
                    name TEXT NOT NULL,
                    meta TEXT NOT NULL,
                    settings_config TEXT,
                    app_type TEXT NOT NULL,
                    is_current INTEGER NOT NULL
                );

                INSERT INTO providers (
                    name, meta, settings_config, app_type, is_current)
                VALUES (
                    $name,
                    $meta,
                    $settingsConfig,
                    'codex',
                    1
                );
                """;
            command.Parameters.AddWithValue("$name", providerName);
            command.Parameters.AddWithValue("$meta", metadata);
            command.Parameters.AddWithValue("$settingsConfig", settingsConfig);
            command.ExecuteNonQuery();
        }
    }

    private sealed class StaticResponseHandler(HttpContent responseContent) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                responseContent.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = responseContent,
                RequestMessage = request,
            });
        }
    }

    private sealed class NeverEndingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
