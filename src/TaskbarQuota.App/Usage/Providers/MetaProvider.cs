using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TaskbarQuota.Usage.Providers
{
    /// <summary>
    /// Meta Muse: server quota (5-hour + weekly percents with resets) polled from
    /// the Muse channel API with the WSL login, refreshed on the standard 60s
    /// usage cadence. When the server is unreachable, falls back to local session
    /// token totals so the card never goes blank without a reason.
    /// </summary>
    public sealed class MetaProvider : IUsageProvider
    {
        public ProviderId Id => ProviderId.Meta;
        public string DisplayName => "Meta";
        public string SessionLabel => "5 hours";
        public string WeeklyLabel => "7 days";
        public BillingKind Billing => BillingKind.Subscription;

        internal const string WslDistro = "Ubuntu-24.04";
        private const string KeyUrl = "https://api.meta.ai/muse-code/key";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly TimeSpan HomesCacheTtl = TimeSpan.FromMinutes(5);
        private static readonly object HomesLock = new();
        private static IReadOnlyList<string> _cachedHomes = Array.Empty<string>();
        private static DateTimeOffset _cachedHomesAt = DateTimeOffset.MinValue;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            try
            {
                return await FetchServerUsageAsync(ct).ConfigureAwait(false);
            }
            catch (ProviderException ex) when (ex.Kind is ProviderErrorKind.AuthRequired or ProviderErrorKind.NotInstalled)
            {
                throw;
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException)
            {
                // Server hiccup: fall back to local session totals instead of a dead card.
                string? sessionsRoot = ResolveSessionsRoot();
                if (sessionsRoot is null)
                    throw new ProviderException(ProviderErrorKind.NotInstalled, "Meta Muse sessions not found in WSL. Run muse once in Ubuntu-24.04.");
                try
                {
                    return BuildSnapshot(sessionsRoot, DateTimeOffset.Now, ct);
                }
                catch (ProviderException)
                {
                    throw new ProviderException(ProviderErrorKind.Other, "Meta quota unreachable and no local sessions to show.");
                }
            }
        }

        internal static async Task<ProviderFetchResult> FetchServerUsageAsync(CancellationToken ct)
        {
            string accessToken = LoadAccessToken();
            using var request = new HttpRequestMessage(HttpMethod.Post, KeyUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Meta login expired. Run /login in muse.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ProviderException(ProviderErrorKind.RateLimited, "Meta API rate limited. Try again later.");
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Meta API returned {(int)response.StatusCode}.");

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                return BuildServerResult(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new ProviderException(ProviderErrorKind.Parse, $"Meta API returned malformed JSON: {ex.Message}", ex);
            }
        }

        internal static ProviderFetchResult BuildServerResult(JsonElement root)
        {
            if (!root.TryGetProperty("subs_usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                throw new ProviderException(ProviderErrorKind.Parse, "Meta API returned no usage.");
            if (!usage.TryGetProperty("window", out var window) || window.ValueKind != JsonValueKind.Object)
                throw new ProviderException(ProviderErrorKind.Parse, "Meta API returned no 5-hour window.");

            var primary = ParseQuotaWindow(window, defaultMinutes: 300, fallbackLabel: "5 hours");
            RateWindow? secondary = null;
            if (usage.TryGetProperty("weekly", out var weekly) && weekly.ValueKind == JsonValueKind.Object)
                secondary = ParseQuotaWindow(weekly, defaultMinutes: 10080, fallbackLabel: "Weekly");

            string? email = root.TryGetProperty("user_email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String
                ? emailEl.GetString()
                : null;
            string? tier = root.TryGetProperty("subs_tier_name", out var tierEl) && tierEl.ValueKind == JsonValueKind.String
                ? tierEl.GetString()
                : null;

            var snapshot = new UsageSnapshot(primary)
            {
                Secondary = secondary,
                LoginMethod = string.IsNullOrWhiteSpace(tier) ? "Muse" : tier,
                Email = email ?? string.Empty,
            };
            return new ProviderFetchResult(snapshot, "meta-api");
        }

        internal static RateWindow ParseQuotaWindow(JsonElement window, int defaultMinutes, string fallbackLabel)
        {
            double percent = 0;
            if (window.TryGetProperty("used_percent", out var percentEl) && percentEl.ValueKind == JsonValueKind.Number)
                percent = percentEl.GetDouble();

            int minutes = defaultMinutes;
            if (window.TryGetProperty("window_duration_mins", out var minsEl) && minsEl.ValueKind == JsonValueKind.Number)
            {
                double m = minsEl.GetDouble();
                if (!double.IsNaN(m) && !double.IsInfinity(m) && m > 0)
                    minutes = (int)Math.Round(m);
            }

            DateTimeOffset? resetAt = null;
            if (window.TryGetProperty("resets_at", out var resetEl) && resetEl.ValueKind == JsonValueKind.Number
                && resetEl.TryGetInt64(out long resetSec) && resetSec > 0)
            {
                try { resetAt = DateTimeOffset.FromUnixTimeSeconds(resetSec); }
                catch (ArgumentOutOfRangeException) { resetAt = null; }
            }

            return new RateWindow(
                Math.Clamp(percent, 0, 100),
                minutes,
                resetAt,
                resetAt is null ? null : OpenCodeProvider.FormatTimeUntil(resetAt.Value),
                label: fallbackLabel);
        }

        internal static string LoadAccessToken() => TryLoadAccessToken()
            ?? throw new ProviderException(ProviderErrorKind.AuthRequired, "Meta login not found. Run /login in muse.");

        internal static string? TryLoadAccessToken(string? homeOverride = null)
        {
            foreach (var home in ResolveHomes(homeOverride))
            {
                try
                {
                    string path = Path.Combine(home, ".config", "muse", "auth.json");
                    if (!File.Exists(path))
                        continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("providers", out var providers)
                        && providers.ValueKind == JsonValueKind.Object
                        && providers.TryGetProperty("meta", out var meta)
                        && meta.ValueKind == JsonValueKind.Object
                        && meta.TryGetProperty("access_token", out var token)
                        && token.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(token.GetString()))
                        return token.GetString()!.Trim();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Unreadable home — try the next one.
                }
            }
            return null;
        }

        internal static bool HasSessions()
        {
            try
            {
                return ResolveSessionsRoot() is not null;
            }
            catch
            {
                return false;
            }
        }

        internal static string? ResolveSessionsRoot()
        {
            foreach (var home in ResolveHomes())
            {
                string sessions = Path.Combine(home, ".local", "share", "muse", "sessions");
                try
                {
                    if (Directory.Exists(sessions))
                        return sessions;
                }
                catch
                {
                    // \\wsl$ unreachable (WSL down) — try the next home.
                }
            }
            return null;
        }

        internal static IReadOnlyList<string> ResolveHomes(string? homeOverride = null)
        {
            if (!string.IsNullOrWhiteSpace(homeOverride))
                return new[] { homeOverride };
            lock (HomesLock)
            {
                if (DateTimeOffset.Now - _cachedHomesAt < HomesCacheTtl)
                    return _cachedHomes;

                var homes = new List<string>();
                try
                {
                    string baseDir = $@"\\wsl$\{WslDistro}\home";
                    if (Directory.Exists(baseDir))
                    {
                        foreach (var dir in Directory.EnumerateDirectories(baseDir))
                            homes.Add(dir);
                    }
                }
                catch
                {
                    // WSL unreachable.
                }
                _cachedHomes = homes;
                _cachedHomesAt = DateTimeOffset.Now;
                return _cachedHomes;
            }
        }

        internal static void ResetHomesCacheForTesting()
        {
            lock (HomesLock)
            {
                _cachedHomes = Array.Empty<string>();
                _cachedHomesAt = DateTimeOffset.MinValue;
            }
        }

        internal static ProviderFetchResult BuildSnapshot(string sessionsRoot, DateTimeOffset now, CancellationToken ct = default)
        {
            long nowMs = now.ToUnixTimeMilliseconds();
            long fiveHoursAgoMs = nowMs - (long)TimeSpan.FromHours(5).TotalMilliseconds;
            long sevenDaysAgoMs = nowMs - TimeSpan.FromDays(7).Ticks / TimeSpan.TicksPerMillisecond;

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            double tokens5h = 0;
            double tokens7d = 0;

            string[] files;
            try
            {
                files = Directory.EnumerateFiles(sessionsRoot, "session.jsonl", SearchOption.AllDirectories).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ProviderException(ProviderErrorKind.Other, $"Meta sessions unreadable: {ex.Message}", ex);
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    AccumulateFile(file, seenIds, fiveHoursAgoMs, sevenDaysAgoMs, ref tokens5h, ref tokens7d);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    // One locked/truncated session must not hide all other usage.
                }
            }

            var snapshot = new UsageSnapshot(new RateWindow(0))
            {
                HasPrimaryWindow = false,
                LoginMethod = "Muse",
                LocalTokens5h = FormatTokens(tokens5h),
                LocalTokens7d = FormatTokens(tokens7d),
            };
            return new ProviderFetchResult(snapshot, "wsl");
        }

        internal static void AccumulateFile(
            string path,
            HashSet<string> seenIds,
            long fiveHoursAgoMs,
            long sevenDaysAgoMs,
            ref double tokens5h,
            ref double tokens7d)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }
                using (doc)
                {
                    AccumulateRecord(doc.RootElement, seenIds, fiveHoursAgoMs, sevenDaysAgoMs, ref tokens5h, ref tokens7d);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("children", out var children)
                        && children.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var child in children.EnumerateArray())
                        {
                            if (child.ValueKind != JsonValueKind.Object
                                || !child.TryGetProperty("record_json", out var recordJson)
                                || recordJson.ValueKind != JsonValueKind.String)
                                continue;
                            try
                            {
                                using var nested = JsonDocument.Parse(recordJson.GetString() ?? "");
                                AccumulateRecord(nested.RootElement, seenIds, fiveHoursAgoMs, sevenDaysAgoMs, ref tokens5h, ref tokens7d);
                            }
                            catch (JsonException)
                            {
                                // Truncated child frame — skip it.
                            }
                        }
                    }
                }
            }
        }

        internal static void AccumulateRecord(
            JsonElement record,
            HashSet<string> seenIds,
            long fiveHoursAgoMs,
            long sevenDaysAgoMs,
            ref double tokens5h,
            ref double tokens7d)
        {
            if (record.ValueKind != JsonValueKind.Object)
                return;
            if (!record.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return;
            if (!payload.TryGetProperty("event", out var evt) || evt.ValueKind != JsonValueKind.Object)
                return;
            if (!evt.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                || !string.Equals(kind.GetString(), "goal_usage_attribution", StringComparison.Ordinal))
                return;
            if (payload.TryGetProperty("usage_family", out var family) && family.ValueKind == JsonValueKind.String
                && !string.Equals(family.GetString(), "provider", StringComparison.OrdinalIgnoreCase))
                return;
            if (!record.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                return;
            if (!seenIds.Add(idEl.GetString() ?? ""))
                return; // same attribution journaled twice (snapshot + live log)

            if (!evt.TryGetProperty("record", out var usageRecord) || usageRecord.ValueKind != JsonValueKind.Object)
                return;
            if (!usageRecord.TryGetProperty("quantity", out var quantity) || quantity.ValueKind != JsonValueKind.Object)
                return;
            if (quantity.TryGetProperty("reported", out var reported)
                && reported.ValueKind is JsonValueKind.False)
                return;

            double tokens = ReadDouble(quantity, "input_tokens")
                + ReadDouble(quantity, "output_tokens")
                + ReadDouble(quantity, "cached_tokens");
            if (tokens <= 0)
                return;

            long recordedMs = ReadRecordedMs(record);
            if (recordedMs <= 0)
                return;
            if (recordedMs >= sevenDaysAgoMs)
                tokens7d += tokens;
            if (recordedMs >= fiveHoursAgoMs)
                tokens5h += tokens;
        }

        internal static long ReadRecordedMs(JsonElement record)
        {
            if (!record.TryGetProperty("recorded_at", out var at) || at.ValueKind != JsonValueKind.Number)
                return 0;
            // Session logs stamp microseconds; tolerate milliseconds too.
            double raw = at.GetDouble();
            if (double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0)
                return 0;
            double ms = raw > 10_000_000_000_000d ? raw / 1000d : raw;
            if (ms > (double)long.MaxValue)
                return 0;
            return (long)ms;
        }

        internal static double ReadDouble(JsonElement obj, string name)
        {
            if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number)
            {
                double v = el.GetDouble();
                if (!double.IsNaN(v) && !double.IsInfinity(v) && v > 0)
                    return v;
            }
            return 0;
        }

        internal static string FormatTokens(double value)
        {
            if (value >= 1_000_000)
                return (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            if (value >= 1000)
                return (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
        }
    }
}
