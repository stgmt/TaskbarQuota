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
              "metadata": {"email": "stigmat.rudnev@gmail.com"},
              "limits": [
                {"id": "zai:credits:5h", "label": "ZAI 5 Hours Credit Quota", "window": {"id": "5h", "label": "5 Hours", "durationMs": 18000000, "resetsAt": 1788810365523}, "amount": {"used": 132, "limit": 2000, "remaining": 1868, "usedFraction": 0.066, "remainingFraction": 0.934, "unit": "credits"}, "status": "ok"}
              ]
            },
            {
              "provider": "google-antigravity",
              "metadata": {"email": "mail.stgmt@gmail.com"},
              "limits": [
                {"id": "google-antigravity:google:default:gemini-weekly", "label": "Usage (Google)", "window": {"id": "weekly", "label": "Weekly", "resetsAt": 1789064397000, "durationMs": 604800000}, "amount": {"used": 93.2625465, "limit": 100, "usedFraction": 0.932625465, "unit": "percent"}, "status": "warning"},
                {"id": "google-antigravity:anthropic:default:3p-weekly", "label": "Usage (Anthropic)", "window": {"id": "weekly", "label": "Weekly", "resetsAt": 1789074320000, "durationMs": 604800000}, "amount": {"used": 71.1, "limit": 100, "usedFraction": 0.711, "unit": "percent"}, "status": "ok"}
              ]
            },
            {
              "provider": "cline-pass",
              "metadata": {"email": "stigmat.rudnev@gmail.com"},
              "limits": [
                {"id": "cline-pass:5h", "label": "ClinePass", "window": {"id": "5h", "label": "5 Hour"}, "amount": {"used": 0, "limit": 100, "usedFraction": 0, "unit": "percent"}, "status": "ok"},
                {"id": "broken", "label": "Broken", "window": {"id": "5h", "label": "5 Hour"}, "status": "ok"}
              ]
            }
          ]
        }
        """;

    [Fact]
    public void BuildResult_MapsEveryLiveLimitToABar()
    {
        var result = OmpProvider.BuildResult(SampleJson);

        // opencode-go #1 is fully eaten on monthly, so the whole account is hidden.
        // Left: oc#2 5h + zai + 2 antigravity + cline = 5; hottest becomes Primary, 4 extras.
        Assert.Equal(4, result.Usage.ExtraRateWindows.Count);
        var ids = result.Usage.ExtraRateWindows.Select(w => w.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains("opencode-go#2:rolling-5h", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("opencode-go:") || id.StartsWith("opencode-go#1:"));
    }

    [Fact]
    public void BuildResult_PrimaryIsHottestLiveWindow()
    {
        var result = OmpProvider.BuildResult(SampleJson);

        Assert.InRange(result.Usage.Primary.UsedPercent, 93.2, 93.3);
        Assert.Contains("Antigravity · Google · Weekly", result.Usage.Primary.Label);
    }

    [Fact]
    public void BuildResult_SameNamedWindowsKeepTheirQualifier()
    {
        var result = OmpProvider.BuildResult(SampleJson);

        var titles = result.Usage.ExtraRateWindows.Select(w => w.Title).ToList();
        Assert.Contains("Antigravity · Anthropic · Weekly", titles);
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
        Assert.Equal("Cline Pass · 5 Hour", cline.Title);
        Assert.Null(cline.Window.ResetAt);
        Assert.Null(cline.Window.ResetDescription);
    }

    [Fact]
    public void BuildResult_DistinctEmailsBecomeAccountTags()
    {
        const string json = """
            {"reports": [
              {"provider": "x", "metadata": {"email": "a@one.com"}, "limits": [
                {"id": "w", "label": "Weekly limit", "window": {"label": "Weekly"}, "amount": {"usedFraction": 0.1}, "status": "ok"}]},
              {"provider": "x", "metadata": {"email": "b@two.com"}, "limits": [
                {"id": "w", "label": "Weekly limit", "window": {"label": "Weekly"}, "amount": {"usedFraction": 0.2}, "status": "ok"}]}
            ]}
            """;

        var result = OmpProvider.BuildResult(json);

        var titles = result.Usage.ExtraRateWindows.Select(w => w.Title).ToList();
        Assert.Contains("X a · Weekly", titles);
        Assert.Contains("X b · Weekly", result.Usage.Primary.Label);
    }

    [Fact]
    public void BuildResult_PartiallyEatenMonthlyAccountIsKept()
    {
        const string json = """
            {"reports": [
              {"provider": "y", "limits": [
                {"id": "monthly", "label": "Monthly limit", "window": {"label": "Monthly"}, "amount": {"usedFraction": 0.5}, "status": "ok"},
                {"id": "5h", "label": "5 Hour limit", "window": {"label": "5 Hour"}, "amount": {"usedFraction": 0.1}, "status": "ok"}]}
            ]}
            """;

        var result = OmpProvider.BuildResult(json);

        Assert.Contains("Monthly", result.Usage.Primary.Label);
        Assert.Single(result.Usage.ExtraRateWindows);
    }

    [Fact]
    public void BuildResult_WeeklyEatenAccountIsHiddenUntilReset()
    {
        const string json = """
            {"reports": [
              {"provider": "v", "limits": [
                {"id": "weekly", "label": "Weekly limit", "window": {"label": "Weekly"}, "amount": {"usedFraction": 1}, "status": "exhausted"},
                {"id": "5h", "label": "5 Hour limit", "window": {"label": "5 Hour"}, "amount": {"usedFraction": 0.3}, "status": "ok"}]},
              {"provider": "w", "limits": [
                {"id": "5h", "label": "5 Hour limit", "window": {"label": "5 Hour"}, "amount": {"usedFraction": 0.1}, "status": "ok"}]}
            ]}
            """;

        var result = OmpProvider.BuildResult(json);

        var titles = result.Usage.ExtraRateWindows.Select(w => w.Title).ToList();
        Assert.DoesNotContain(titles, t => t.StartsWith("V "));
        Assert.Single(titles);
        Assert.Equal("W · 5 Hour", titles[0]);
    }

    [Fact]
    public void BuildResult_AllEatenFallsBackToShowingEverything()
    {
        const string json = """
            {"reports": [
              {"provider": "z", "limits": [
                {"id": "monthly", "label": "Monthly limit", "window": {"label": "Monthly"}, "amount": {"usedFraction": 1}, "status": "exhausted"}]}
            ]}
            """;

        var result = OmpProvider.BuildResult(json);

        Assert.Single(result.Usage.ExtraRateWindows);
        Assert.InRange(result.Usage.Primary.UsedPercent, 99.9, 100.1);
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
