using System.Net;
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

    private sealed class BalanceFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"CodexMonitor.BalanceTests-{Guid.NewGuid():N}");

        private readonly HttpClient _httpClient;

        public BalanceFixture(
            HttpContent responseContent,
            TimeSpan? responseBodyTimeout = null,
            int maximumResponseBytes = 1024)
        {
            Directory.CreateDirectory(_root);
            string databasePath = Path.Combine(_root, "cc-switch.db");
            CreateDatabase(databasePath);

            _httpClient = new HttpClient(new StaticResponseHandler(responseContent))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };

            Service = new UsageBalanceService(
                _httpClient,
                databasePath,
                databaseTimeout: TimeSpan.FromSeconds(1),
                responseHeadersTimeout: TimeSpan.FromSeconds(1),
                responseBodyTimeout: responseBodyTimeout ?? TimeSpan.FromSeconds(1),
                maximumResponseBytes);
        }

        public UsageBalanceService Service { get; }

        public void Dispose()
        {
            Service.Dispose();
            _httpClient.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
        }

        private static void CreateDatabase(string databasePath)
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
                    'Test provider',
                    '{"usage_script":{"enabled":"true","baseUrl":"https://example.test","code":"{{baseUrl}}/v1/usage"}}',
                    '{"auth":{"OPENAI_API_KEY":"test-key"}}',
                    'codex',
                    1
                );
                """;
            command.ExecuteNonQuery();
        }
    }

    private sealed class StaticResponseHandler(HttpContent responseContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
