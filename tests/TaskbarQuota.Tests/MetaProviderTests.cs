using System;
using System.IO;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class MetaProviderTests
{
    private static string WriteSessionDir(DateTimeOffset now)
    {
        string dir = Path.Combine(Path.GetTempPath(), "meta-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        long us = now.ToUnixTimeMilliseconds() * 1000;
        long oldUs = now.AddDays(-3).ToUnixTimeMilliseconds() * 1000;

        // Top-level attribution inside the 5h window.
        string top = "{\"schema_version\":1,\"id\":\"rec-top-1\",\"recorded_at\":" + us
            + ",\"payload\":{\"usage_family\":\"provider\",\"event\":{\"kind\":\"goal_usage_attribution\","
            + "\"record\":{\"quantity\":{\"input_tokens\":10000,\"output_tokens\":2000,\"cached_tokens\":500}}}}}";
        // Nested child frame, also inside the window but a duplicate id away in another file.
        string nested = "{\"schema_version\":1,\"id\":\"frame-1\",\"recorded_at\":" + us
            + ",\"children\":[{\"record_json\":\"{\\\"schema_version\\\":1,\\\"id\\\":\\\"rec-nested-1\\\","
            + "\\\"recorded_at\\\":" + us + ",\\\"payload\\\":{\\\"usage_family\\\":\\\"provider\\\","
            + "\\\"event\\\":{\\\"kind\\\":\\\"goal_usage_attribution\\\",\\\"record\\\":{\\\"quantity\\\":"
            + "{\\\"input_tokens\\\":4000,\\\"output_tokens\\\":1000}}}}}\"}]}";
        // Old record: inside 7d, outside 5h.
        string old = "{\"schema_version\":1,\"id\":\"rec-old-1\",\"recorded_at\":" + oldUs
            + ",\"payload\":{\"event\":{\"kind\":\"goal_usage_attribution\","
            + "\"record\":{\"quantity\":{\"input_tokens\":100000,\"output_tokens\":20000}}}}}";
        // Noise: wrong family, unreported, malformed.
        string noise = "{\"schema_version\":1,\"id\":\"rec-noise-1\",\"recorded_at\":" + us
            + ",\"payload\":{\"usage_family\":\"other\",\"event\":{\"kind\":\"goal_usage_attribution\","
            + "\"record\":{\"quantity\":{\"input_tokens\":999999}}}}}";
        string unreported = "{\"schema_version\":1,\"id\":\"rec-unrep-1\",\"recorded_at\":" + us
            + ",\"payload\":{\"event\":{\"kind\":\"goal_usage_attribution\","
            + "\"record\":{\"quantity\":{\"input_tokens\":888888,\"reported\":false}}}}}";
        File.WriteAllText(Path.Combine(dir, "session.jsonl"),
            top + "\n" + nested + "\n" + old + "\n" + noise + "\n" + unreported + "\nnot json\n");

        // Second file duplicates rec-top-1 (journaled twice) — must not double count.
        string dup = "{\"schema_version\":1,\"id\":\"rec-top-1\",\"recorded_at\":" + us
            + ",\"payload\":{\"event\":{\"kind\":\"goal_usage_attribution\","
            + "\"record\":{\"quantity\":{\"input_tokens\":10000,\"output_tokens\":2000,\"cached_tokens\":500}}}}}";
        string sub = Path.Combine(dir, "subagent");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "session.jsonl"), dup + "\n");
        return dir;
    }

    [Fact]
    public void BuildSnapshot_SumsWindowsWithoutDoubleCounting()
    {
        var now = DateTimeOffset.Now;
        string dir = WriteSessionDir(now);
        try
        {
            var result = MetaProvider.BuildSnapshot(dir, now);

            // 5h: 12500 (top) + 5000 (nested) = 17500 -> 17.5k
            Assert.Equal("17.5k", result.Usage.LocalTokens5h);
            // 7d adds the old 120000 -> 137500 -> 137.5k
            Assert.Equal("137.5k", result.Usage.LocalTokens7d);
            Assert.False(result.Usage.HasPrimaryWindow);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildSnapshot_MissingDirThrowsNotInstalled()
    {
        var ex = Assert.Throws<ProviderException>(() =>
            MetaProvider.BuildSnapshot(Path.Combine(Path.GetTempPath(), "meta-nope-" + Guid.NewGuid().ToString("N")), DateTimeOffset.Now));
        Assert.Equal(ProviderErrorKind.Other, ex.Kind);
    }

    [Fact]
    public void BuildServerResult_MapsQuotaWindowsToBars()
    {
        const string json = """
            {"user_email": "mail.stgmt@gmail.com", "subs_tier_name": "Muse Code Power Usage",
             "subs_usage": {
               "window": {"used_percent": 15, "window_duration_mins": 300, "resets_at": 1788845673},
               "weekly": {"used_percent": 59, "resets_at": 1789344000}}}
            """;

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = MetaProvider.BuildServerResult(doc.RootElement);

        Assert.InRange(result.Usage.Primary.UsedPercent, 14.9, 15.1);
        Assert.Equal(300, result.Usage.Primary.WindowMinutes);
        Assert.NotNull(result.Usage.Primary.ResetAt);
        Assert.NotNull(result.Usage.Secondary);
        Assert.InRange(result.Usage.Secondary!.UsedPercent, 58.9, 59.1);
        Assert.Equal("mail.stgmt@gmail.com", result.Usage.Email);
        Assert.True(result.Usage.HasPrimaryWindow);
    }

    [Fact]
    public void BuildServerResult_MissingUsageThrowsParse()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{}");
        var ex = Assert.Throws<ProviderException>(() => MetaProvider.BuildServerResult(doc.RootElement));
        Assert.Equal(ProviderErrorKind.Parse, ex.Kind);
    }

    [Fact]
    public void FormatTokens_FormatsCounts()
    {
        Assert.Equal("0", MetaProvider.FormatTokens(0));
        Assert.Equal("999", MetaProvider.FormatTokens(999));
        Assert.Equal("12.3k", MetaProvider.FormatTokens(12345));
        Assert.Equal("1.2M", MetaProvider.FormatTokens(1234567));
    }
}
