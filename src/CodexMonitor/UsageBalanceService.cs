#nullable enable

using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodexMonitor;

internal sealed class UsageBalanceService : IDisposable
{
    private const int DefaultMaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan DefaultDatabaseTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DefaultResponseHeadersTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DefaultResponseBodyTimeout = TimeSpan.FromSeconds(12);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _databasePath;
    private readonly TimeSpan _databaseTimeout;
    private readonly TimeSpan _responseHeadersTimeout;
    private readonly TimeSpan _responseBodyTimeout;
    private readonly int _maximumResponseBytes;

    public UsageBalanceService()
        : this(
            CreateHttpClient(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cc-switch",
                "cc-switch.db"),
            DefaultDatabaseTimeout,
            DefaultResponseHeadersTimeout,
            DefaultResponseBodyTimeout,
            DefaultMaximumResponseBytes,
            ownsHttpClient: true)
    {
    }

    internal UsageBalanceService(
        HttpClient httpClient,
        string databasePath,
        TimeSpan databaseTimeout,
        TimeSpan responseHeadersTimeout,
        TimeSpan responseBodyTimeout,
        int maximumResponseBytes)
        : this(
            httpClient,
            databasePath,
            databaseTimeout,
            responseHeadersTimeout,
            responseBodyTimeout,
            maximumResponseBytes,
            ownsHttpClient: false)
    {
    }

    private UsageBalanceService(
        HttpClient httpClient,
        string databasePath,
        TimeSpan databaseTimeout,
        TimeSpan responseHeadersTimeout,
        TimeSpan responseBodyTimeout,
        int maximumResponseBytes,
        bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(databaseTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(responseHeadersTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(responseBodyTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResponseBytes);

        _httpClient = httpClient;
        _databasePath = databasePath;
        _databaseTimeout = databaseTimeout;
        _responseHeadersTimeout = responseHeadersTimeout;
        _responseBodyTimeout = responseBodyTimeout;
        _maximumResponseBytes = maximumResponseBytes;
        _ownsHttpClient = ownsHttpClient;
    }

    public Task<BalanceSnapshot> ReadAsync() => ReadAsync(CancellationToken.None);

    public async Task<BalanceSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_databasePath))
        {
            return NotConfigured("API", "未找到 CCSwitch 配置");
        }

        string providerName;
        string metadata;
        string settingsConfig;

        try
        {
            using var databaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            databaseCancellation.CancelAfter(_databaseTimeout);
            CancellationToken databaseToken = databaseCancellation.Token;

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 2,
            }.ToString();

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(databaseToken).ConfigureAwait(false);

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = """
                SELECT name, meta, settings_config
                FROM providers
                WHERE app_type = 'codex' AND is_current = 1
                LIMIT 1
                """;

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(databaseToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(databaseToken).ConfigureAwait(false))
            {
                return NotConfigured("API", "CCSwitch 没有当前 Codex 供应商");
            }

            providerName = reader.IsDBNull(0) ? "API" : reader.GetString(0);
            metadata = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            settingsConfig = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return NotConfigured("API", "CCSwitch 配置读取超时");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return NotConfigured("API", $"CCSwitch 配置读取失败：{exception.Message}");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument metadataJson = JsonDocument.Parse(metadata);
            if (!metadataJson.RootElement.TryGetProperty("usage_script", out JsonElement usageScript)
                || !GetString(usageScript, "enabled", "false")
                    .Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                if (providerName.Equals("OpenAI Official", StringComparison.OrdinalIgnoreCase)
                    && IsOfficialOAuth(settingsConfig))
                {
                    return new BalanceSnapshot(
                        providerName,
                        null,
                        "USD",
                        null,
                        null,
                        "官方 OAuth 未提供余额查询",
                        DateTime.Now,
                        IsConfigured: true);
                }

                return NotConfigured(providerName, "该供应商未启用余额查询");
            }

            string baseUrl = GetString(usageScript, "baseUrl", string.Empty).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = ExtractBaseUrl(settingsConfig).TrimEnd('/');
            }

            string apiKey = GetString(usageScript, "apiKey", string.Empty);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = ExtractApiKey(settingsConfig);
            }

            string usagePath = ExtractUsagePath(GetString(usageScript, "code", string.Empty))
                ?? "/v1/usage";

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            {
                return NotConfigured(providerName, "余额查询缺少地址或 API Key");
            }

            if (!Uri.TryCreate(baseUrl + usagePath, UriKind.Absolute, out Uri? usageUri))
            {
                return NotConfigured(providerName, "余额接口地址无效");
            }

            if (usageUri.Scheme != Uri.UriSchemeHttps && !usageUri.IsLoopback)
            {
                return NotConfigured(providerName, "余额接口不是安全连接");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, usageUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("CodexMonitor/1.0");

            using var headersCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headersCancellation.CancelAfter(_responseHeadersTimeout);

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersCancellation.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return ConfiguredError(providerName, $"余额接口返回 {(int)response.StatusCode}");
            }

            using var bodyCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bodyCancellation.CancelAfter(_responseBodyTimeout);
            byte[] responseBody = await ReadResponseBodyAsync(
                    response.Content,
                    _maximumResponseBytes,
                    bodyCancellation.Token)
                .ConfigureAwait(false);

            using JsonDocument responseJson = JsonDocument.Parse(responseBody);
            JsonElement root = responseJson.RootElement;
            decimal? remaining = GetDecimal(root, "remaining")
                ?? GetNestedDecimal(root, "quota", "remaining")
                ?? GetDecimal(root, "balance");
            string unit = GetString(root, "unit", "USD").ToUpperInvariant();
            string planName = GetString(root, "planName", string.Empty);
            decimal? todayCost = GetNestedDecimal(root, "usage", "today", "actual_cost")
                ?? GetNestedDecimal(root, "usage", "today", "cost");

            return new BalanceSnapshot(
                providerName,
                remaining,
                unit,
                string.IsNullOrWhiteSpace(planName) ? null : planName,
                todayCost,
                remaining.HasValue ? null : "接口未返回可识别的余额",
                DateTime.Now,
                IsConfigured: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ConfiguredError(providerName, "余额查询超时");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ConfiguredError(providerName, $"余额查询失败：{exception.Message}");
        }
    }

    private static async Task<byte[]> ReadResponseBodyAsync(
        HttpContent content,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        long? contentLength = content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maximumResponseBytes)
        {
            throw new InvalidDataException($"响应体超过 {maximumResponseBytes} 字节上限");
        }

        int initialCapacity = contentLength.HasValue
            && contentLength.Value >= 0
            && contentLength.Value <= maximumResponseBytes
            ? (int)contentLength.Value
            : 0;

        await using Stream responseStream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var bufferStream = new MemoryStream(initialCapacity);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            while (true)
            {
                int bytesRead = await responseStream
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    return bufferStream.ToArray();
                }

                if (bufferStream.Length + bytesRead > maximumResponseBytes)
                {
                    throw new InvalidDataException($"响应体超过 {maximumResponseBytes} 字节上限");
                }

                await bufferStream
                    .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static BalanceSnapshot NotConfigured(string providerName, string error) =>
        new(providerName, null, "USD", null, null, error, DateTime.Now, IsConfigured: false);

    private static BalanceSnapshot ConfiguredError(string providerName, string error) =>
        new(providerName, null, "USD", null, null, error, DateTime.Now, IsConfigured: true);

    private static string GetString(JsonElement element, string name, string fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : value.ToString();
    }

    private static decimal? GetDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
        {
            return number;
        }

        return decimal.TryParse(
            value.ToString(),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out decimal parsed)
            ? parsed
            : null;
    }

    private static decimal? GetNestedDecimal(JsonElement element, params string[] path)
    {
        JsonElement value = element;
        foreach (string propertyName in path)
        {
            if (!value.TryGetProperty(propertyName, out value))
            {
                return null;
            }
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
        {
            return number;
        }

        return decimal.TryParse(
            value.ToString(),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out decimal parsed)
            ? parsed
            : null;
    }

    private static string? ExtractUsagePath(string code)
    {
        Match match = Regex.Match(
            code,
            "\\{\\{baseUrl\\}\\}(?<path>/[^\"']+)",
            RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups["path"].Value : null;
    }

    private static string ExtractApiKey(string settingsConfig)
    {
        if (string.IsNullOrWhiteSpace(settingsConfig))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument settingsJson = JsonDocument.Parse(settingsConfig);
            if (!settingsJson.RootElement.TryGetProperty("auth", out JsonElement authentication))
            {
                return string.Empty;
            }

            string apiKey = GetString(authentication, "OPENAI_API_KEY", string.Empty);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = GetString(authentication, "apiKey", string.Empty);
            }

            return apiKey.Trim();
        }
        catch (JsonException)
        {
            // Some CCSwitch versions may leave settings_config as non-JSON.
            return string.Empty;
        }
    }

    private static bool IsOfficialOAuth(string settingsConfig)
    {
        if (!TryGetAuthentication(settingsConfig, out JsonDocument? settingsJson, out JsonElement authentication))
        {
            return false;
        }

        using (settingsJson)
        {
            string authenticationMode = GetString(authentication, "auth_mode", string.Empty);
            bool hasAccessToken = authentication.TryGetProperty("tokens", out JsonElement tokens)
                && !string.IsNullOrWhiteSpace(GetString(tokens, "access_token", string.Empty));
            bool hasApiKey = !string.IsNullOrWhiteSpace(
                GetString(authentication, "OPENAI_API_KEY", string.Empty));

            return !string.IsNullOrWhiteSpace(authenticationMode)
                && hasAccessToken
                && !hasApiKey;
        }
    }

    private static string ExtractBaseUrl(string settingsConfig)
    {
        if (string.IsNullOrWhiteSpace(settingsConfig))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument settingsJson = JsonDocument.Parse(settingsConfig);
            string configuration = GetString(settingsJson.RootElement, "config", string.Empty);
            return ExtractModelProviderBaseUrl(configuration);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string ExtractModelProviderBaseUrl(string configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return string.Empty;
        }

        string? modelProvider = null;
        string? currentSection = null;
        string[] lines = configuration.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (TryReadTomlSection(trimmed, out string? section))
            {
                currentSection = section;
                continue;
            }

            if (currentSection is null
                && TryReadTomlStringAssignment(trimmed, "model_provider", out string? value))
            {
                modelProvider = value;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(modelProvider))
        {
            return string.Empty;
        }

        currentSection = null;
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (TryReadTomlSection(trimmed, out string? section))
            {
                currentSection = section;
                continue;
            }

            if (IsModelProviderSection(currentSection, modelProvider)
                && TryReadTomlStringAssignment(trimmed, "base_url", out string? baseUrl))
            {
                return baseUrl ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool TryReadTomlSection(string line, out string? section)
    {
        section = null;
        if (line.Length < 3 || line[0] != '[' || line[^1] != ']')
        {
            return false;
        }

        section = line[1..^1].Trim();
        return section.Length > 0;
    }

    private static bool IsModelProviderSection(string? section, string modelProvider)
    {
        const string prefix = "model_providers.";
        if (section is null || !section.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string providerSegment = section[prefix.Length..].Trim();
        return TryReadTomlString(providerSegment, out string? sectionProvider)
            && string.Equals(sectionProvider, modelProvider, StringComparison.Ordinal);
    }

    private static bool TryReadTomlStringAssignment(
        string line,
        string expectedKey,
        out string? value)
    {
        value = null;
        if (line.Length == 0 || line[0] == '#')
        {
            return false;
        }

        int equalsIndex = line.IndexOf('=');
        if (equalsIndex <= 0
            || !line[..equalsIndex].Trim().Equals(expectedKey, StringComparison.Ordinal))
        {
            return false;
        }

        return TryReadTomlString(line[(equalsIndex + 1)..].Trim(), out value);
    }

    private static bool TryReadTomlString(string text, out string? value)
    {
        value = null;
        if (text.Length == 0)
        {
            return false;
        }

        if (text[0] == '\'')
        {
            int closingQuote = text.IndexOf('\'', 1);
            if (closingQuote < 0)
            {
                return false;
            }

            value = text[1..closingQuote];
            return true;
        }

        if (text[0] == '"')
        {
            int closingQuote = FindClosingDoubleQuote(text);
            if (closingQuote < 0)
            {
                return false;
            }

            try
            {
                value = JsonSerializer.Deserialize<string>(text[..(closingQuote + 1)]);
                return value is not null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        int commentIndex = text.IndexOf('#');
        value = (commentIndex >= 0 ? text[..commentIndex] : text).Trim();
        return value.Length > 0;
    }

    private static int FindClosingDoubleQuote(string text)
    {
        bool escaped = false;
        for (int index = 1; index < text.Length; index++)
        {
            char character = text[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryGetAuthentication(
        string settingsConfig,
        out JsonDocument? settingsJson,
        out JsonElement authentication)
    {
        settingsJson = null;
        authentication = default;
        if (string.IsNullOrWhiteSpace(settingsConfig))
        {
            return false;
        }

        try
        {
            settingsJson = JsonDocument.Parse(settingsConfig);
            if (settingsJson.RootElement.TryGetProperty("auth", out authentication))
            {
                return true;
            }

            settingsJson.Dispose();
            settingsJson = null;
            return false;
        }
        catch (JsonException)
        {
            settingsJson?.Dispose();
            settingsJson = null;
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static HttpClient CreateHttpClient() => new()
    {
        // Each phase has its own timeout below so a slow response body cannot
        // consume the request-header timeout or continue without cancellation.
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
