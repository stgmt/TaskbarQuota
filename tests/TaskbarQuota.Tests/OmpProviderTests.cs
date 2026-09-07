using System.Linq;
using TaskbarQuota.Usage;
using TaskbarQuota.Usage.Providers;

namespace TaskbarQuota.Tests;

public class OmpProviderTests
{
    private const string SampleJson = """
        {
          "generatedAt": 1788798931030,
          "reports": [
            {
              "provider": "opencode-go",
              "limits": [
                {"id": "rolling-5h", "label": "5 Hour limit", "window": {"id": "5h", "label": "5 Hour", "resetsAt": 1788816807123, "durationMs": 18000000}, "amount": {"used": 0, "usedFraction": 0, "remainingFraction": 1, "unit": "percent"}, "status": "ok"},
                {"id": "weekly", "label": "Weekly limit", "window": {"id": "7d", "label": "Weekly", "resetsAt": 1789344000123, "durationMs": 604800000}, "amount": {"used": 0, "usedFraction": 0, "remainingFraction": 1, "unit": "percent"}, "status": "ok"},
                {"id": "monthly", "label": "Monthly limit", "window": {"id": "monthly", "label": "Monthly", "resetsAt": 1789520949123}, "amount": {"used": 100, "usedFraction": 1, "remainingFraction": 0, "unit": "percent"}, "status": "exhausted"}
              ]
            },
            {
              "provider": "opencode-go",
              "limits": [
                {"id": "rolling-5h", "label": "5 Hour limit", "window": {"id": "5h", "label": "5 Hour", "resetsAt": 1788816807199, "durationMs": 18000000}, "amount": {"used": 0, "usedFraction": 0, "remainingFraction": 1, "unit": "percent"}, "status": "ok"}
              ]
            },
            {
              "provider": "zai",
              "limits": [
                {"id": "zai:credits:5h", "label": "ZAI 5 Hours Credit Quota", "window": {"id": "5h", "label": "5 Hours", "durationMs": 18000000, "resetsAt": 1788810365523}, "amount": {"used": 132, "limit": 2000, "remaining": 1868, "usedFraction": 0.066, "remainingFraction": 0.934, "unit": "credits"}, "status": "ok"}
              ]
            },
            {
              "provider": "google-antigravity",
              "limits": [
                {"id": "google-antigravity:google:default:gemini-weekly", "label": "Usage (Google)", "window": {"id": "weekly", "label": "Weekly", "resetsAt": 1789064397000, "durationMs": 604800000}, "amount": {"used": 93.2625465, "limit": 100, "usedFraction": 0.932625465, "unit": "percent"}, "status": "warning"}
              ]
            },
            {
              "provider": "cline-pass",
              "limits": [
                {"id": "cline-pass:5h", "label": "ClinePass", "window": {"id": "5h", "label": "5 Hour"}, "amount": {"used": 0, "limit": 100, "usedFraction": 0, "unit": "percent"}, "status": "ok"},
                {"id": "broken", "label": "Broken", "window": {"id": "5h", "label": "5 Hour"}, "status": "ok"}
              ]
            }
          ]
        }
        """;

    [Fact]
    public void BuildResult_MapsEveryLimitToABar()
    {
        var result = OmpProvider.BuildResult(SampleJson);

        // 7 parseable limits (broken entry without amount is skipped):
        // hottest becomes Primary, the other 6 land in the expandable list.
        Assert.Equal(6, result.Usage.ExtraRateWindows.Count);
        var ids = result.Usage.ExtraRateWindows.Select(w => w.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // Second opencode-go account gets a suffixed id, not a collision.
        Assert.Contains("opencode-go:rolling-5h#2", ids);
    }

    [Fact]
    public void BuildResult_PrimaryIsHottestWindow()
    {
        var result = OmpProvider.BuildResult(SampleJson);

        // opencode-go monthly is exhausted at 100% — hotter than Antigravity weekly at 93.3%.
        Assert.InRange(result.Usage.Primary.UsedPercent, 99.9, 100.1);
        Assert.Contains("OpenCode Go", result.Usage.Primary.Label);
    }

    [Fact]
    public void BuildResult_CreditWindowShowsCounts()
    {
        var result = OmpProvider.BuildResult(SampleJson);

        var zai = result.Usage.ExtraRateWindows.First(w => w.Id == "zai:zai:credits:5h");
        Assert.Contains("132", zai.Title);
        Assert.Contains("2k", zai.Title);
        Assert.InRange(zai.Window.UsedPercent, 6.5, 6.7);
    }

    [Fact]
    public void BuildResult_MissingResetHasNoCountdown()
    {
        var result = OmpProvider.BuildResult(SampleJson);

        var cline = result.Usage.ExtraRateWindows.First(w => w.Id == "cline-pass:cline-pass:5h");
        Assert.Null(cline.Window.ResetAt);
        Assert.Null(cline.Window.ResetDescription);
    }

    [Fact]
    public void BuildResult_MalformedJsonThrowsParse()
    {
        var ex = Assert.Throws<ProviderException>(() => OmpProvider.BuildResult("not json"));
        Assert.Equal(ProviderErrorKind.Parse, ex.Kind);
    }

    [Fact]
    public void BuildResult_NoReportsThrowsParse()
    {
        var ex = Assert.Throws<ProviderException>(() => OmpProvider.BuildResult("{\"reports\":[]}"));
        Assert.Equal(ProviderErrorKind.Parse, ex.Kind);
    }
}
