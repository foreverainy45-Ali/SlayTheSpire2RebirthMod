using System.Security.Cryptography;
using System.Text.Json;

namespace SeedRestart.Core;

public sealed class QuoteCatalog
{
    private readonly string[] _quotes;
    private int _lastIndex = -1;

    private QuoteCatalog(string[] quotes, string? error)
    {
        _quotes = quotes;
        Error = error;
    }

    public int Count => _quotes.Length;
    public string? Error { get; }
    public IReadOnlyList<string> Quotes => _quotes;

    public static QuoteCatalog Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new([], $"语录配置不存在：{path}");
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new([], $"读取语录配置失败：{e.Message}");
        }
    }

    public static QuoteCatalog Parse(string json)
    {
        try
        {
            var config = JsonSerializer.Deserialize<QuoteConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (config?.Quotes == null) return new([], "语录配置缺少 quotes 数组");
            var quotes = config.Quotes
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q.Trim())
                .Where(q => q.Length <= 160)
                .Distinct(StringComparer.Ordinal)
                .Take(200)
                .ToArray();
            return quotes.Length == 0
                ? new([], "语录配置没有有效文本")
                : new(quotes, null);
        }
        catch (JsonException e)
        {
            return new([], $"语录配置 JSON 无效：{e.Message}");
        }
    }

    public string? Next()
    {
        if (_quotes.Length == 0) return null;
        if (_quotes.Length == 1) return _quotes[_lastIndex = 0];
        int index;
        do index = RandomNumberGenerator.GetInt32(_quotes.Length);
        while (index == _lastIndex);
        _lastIndex = index;
        return _quotes[index];
    }

    private sealed class QuoteConfig
    {
        public string[]? Quotes { get; init; }
    }
}
