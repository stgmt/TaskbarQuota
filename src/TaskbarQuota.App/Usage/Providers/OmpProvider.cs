using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaskbarQuota.Usage.Providers
{
    /// <summary>
    /// Oh My Pi aggregate: runs `omp usage --json` locally and exposes every limit OMP
    /// tracks as a single card. No extra login — OMP already holds all sessions.
    /// </summary>
    public sealed class OmpProvider : IUsageProvider
    {
        public ProviderId Id => ProviderId.Omp;
        public string DisplayName => "OMP";
        public string SessionLabel => "Hottest window";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            string json = await RunOmpUsageAsync(ct).ConfigureAwait(false);
            return BuildResult(json);
        }

        internal static async Task<string> RunOmpUsageAsync(CancellationToken ct)
        {
            var startInfo = new ProcessStartInfo("omp", "usage --json")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    throw new ProviderException(ProviderErrorKind.Other, "OMP did not start. Make sure Oh My Pi is installed.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new ProviderException(ProviderErrorKind.NotInstalled, "OMP CLI not found. Install Oh My Pi to see usage here.");
            }

            using var timeoutCts = new CancellationTokenSource(FetchTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var killOnCancel = linkedCts.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
            });

            string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            if (timeoutCts.IsCancellationRequested)
                throw new ProviderException(ProviderErrorKind.Timeout, "OMP took too long to answer. Try again later.");
            if (process.ExitCode != 0)
            {
                var detail = stderr.Trim();
                if (detail.Length > 200) detail = detail.Substring(0, 200);
                throw new ProviderException(ProviderErrorKind.Other,
                    string.IsNullOrEmpty(detail) ? $"OMP exited with code {process.ExitCode}." : $"OMP error: {detail}");
            }
            if (string.IsNullOrWhiteSpace(stdout))
                throw new ProviderException(ProviderErrorKind.Parse, "OMP returned no data.");
            return stdout;
        }

        internal static ProviderFetchResult BuildResult(string json)
        {
            using var doc = Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("reports", out var reports) || reports.ValueKind != JsonValueKind.Array)
                throw new ProviderException(ProviderErrorKind.Parse, "OMP response has no reports.");

            var extras = new List<NamedRateWindow>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var accountCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string? hottestTitle = null;
            string? hottestExtraId = null;
            RateWindow? hottest = null;

            foreach (var report in reports.EnumerateArray())
            {
                string provider = report.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() ?? "omp"
                    : "omp";
                if (!report.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var limit in limits.EnumerateArray())
                {
                    string limitId = limit.TryGetProperty("id", out var lid) && lid.ValueKind == JsonValueKind.String
                        ? lid.GetString() ?? "limit"
                        : "limit";
                    string baseId = $"{provider}:{limitId}";
                    int occurrence = accountCounters.TryGetValue(baseId, out int n) ? n + 1 : 1;
                    accountCounters[baseId] = occurrence;
                    string id = occurrence == 1 ? baseId : $"{baseId}#{occurrence}";

                    var window = BuildWindow(limit);
                    if (window is null)
                        continue;

                    string title = BuildTitle(provider, limit, occurrence);
                    if (!seenIds.Add(id))
                        continue;
                    extras.Add(new NamedRateWindow(id, title, window));

                    if (hottest is null || window.UsedPercent > hottest.UsedPercent)
                    {
                        hottest = window;
                        hottestTitle = title;
                        hottestExtraId = id;
                    }
                }
            }

            if (hottest is null)
                throw new ProviderException(ProviderErrorKind.Parse, "OMP reported no usable limits.");

            var primary = new RateWindow(hottest.UsedPercent, hottest.WindowMinutes, hottest.ResetAt, hottest.ResetDescription, label: hottestTitle);
            var snapshot = new UsageSnapshot(primary) { LoginMethod = "OMP" };
            foreach (var extra in extras)
            {
                if (extra.Id == hottestExtraId)
                    continue; // primary already carries the hottest window; avoid a duplicate first bar
                snapshot.ExtraRateWindows.Add(extra);
            }
            // If everything collapsed to the primary (single-limit OMP), still show its bar in the list.
            if (snapshot.ExtraRateWindows.Count == 0)
                snapshot.ExtraRateWindows.Add(new NamedRateWindow(hottestExtraId ?? "omp", hottestTitle ?? "OMP", hottest));
            return new ProviderFetchResult(snapshot, "omp");
        }

        private static JsonDocument Parse(string json)
        {
            try
            {
                return JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new ProviderException(ProviderErrorKind.Parse, $"OMP returned malformed JSON: {ex.Message}", ex);
            }
        }

        private static RateWindow? BuildWindow(JsonElement limit)
        {
            double percent;
            if (limit.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Object)
            {
                if (amount.TryGetProperty("usedFraction", out var frac) && frac.ValueKind == JsonValueKind.Number)
                {
                    percent = frac.GetDouble() * 100;
                }
                else if (amount.TryGetProperty("used", out var used) && used.ValueKind == JsonValueKind.Number
                    && amount.TryGetProperty("limit", out var cap) && cap.ValueKind == JsonValueKind.Number
                    && cap.GetDouble() > 0)
                {
                    percent = used.GetDouble() / cap.GetDouble() * 100;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }

            int? windowMinutes = null;
            DateTimeOffset? resetAt = null;
            if (limit.TryGetProperty("window", out var window) && window.ValueKind == JsonValueKind.Object)
            {
                if (window.TryGetProperty("durationMs", out var dur) && dur.ValueKind == JsonValueKind.Number)
                {
                    double minutes = dur.GetDouble() / 60000d;
                    if (!double.IsNaN(minutes) && !double.IsInfinity(minutes) && minutes > 0)
                        windowMinutes = (int)Math.Round(minutes);
                }
                if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number
                    && reset.TryGetInt64(out long resetMs) && resetMs > 0)
                {
                    try { resetAt = DateTimeOffset.FromUnixTimeMilliseconds(resetMs); }
                    catch (ArgumentOutOfRangeException) { resetAt = null; }
                }
            }

            return new RateWindow(
                Math.Clamp(percent, 0, 100),
                windowMinutes,
                resetAt,
                resetAt is null ? null : OpenCodeProvider.FormatTimeUntil(resetAt.Value));
        }

        private static string BuildTitle(string provider, JsonElement limit, int occurrence)
        {
            string service = ServiceShortName(provider);
            if (occurrence > 1)
                service = $"{service} #{occurrence}";

            string limitLabel = limit.TryGetProperty("label", out var ll) && ll.ValueKind == JsonValueKind.String
                ? (ll.GetString() ?? "").Trim()
                : "";
            string windowLabel = limit.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Object
                && w.TryGetProperty("label", out var wl) && wl.ValueKind == JsonValueKind.String
                ? (wl.GetString() ?? "").Trim()
                : "";

            string amountSuffix = "";
            if (limit.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Object
                && amount.TryGetProperty("unit", out var unit) && unit.ValueKind == JsonValueKind.String
                && string.Equals(unit.GetString(), "credits", StringComparison.OrdinalIgnoreCase)
                && amount.TryGetProperty("used", out var used) && used.ValueKind == JsonValueKind.Number
                && amount.TryGetProperty("limit", out var cap) && cap.ValueKind == JsonValueKind.Number)
            {
                amountSuffix = $" {FormatCount(used.GetDouble())}/{FormatCount(cap.GetDouble())}";
            }

            // Prefer the window label ("5 Hour", "Weekly") — short and uniform across services.
            string core = !string.IsNullOrEmpty(windowLabel) ? windowLabel : limitLabel;
            if (string.IsNullOrEmpty(core))
                core = "Usage";
            return $"{service} · {core}{amountSuffix}";
        }

        private static string ServiceShortName(string provider) => provider switch
        {
            "google-antigravity" => "Antigravity",
            "opencode-go" => "OpenCode Go",
            "xai-oauth" => "Grok",
            "zai" => "Z.ai",
            "cline-pass" => "Cline Pass",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(provider.Replace('-', ' ').Replace('_', ' ')),
        };

        private static string FormatCount(double value)
        {
            if (value >= 1000)
                return (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        }
    }
}
