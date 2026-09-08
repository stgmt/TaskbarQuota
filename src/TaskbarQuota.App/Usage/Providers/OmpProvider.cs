using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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

            // OMP can track several accounts per service (e.g. two OpenCode Go logins).
            // Reports carry no shared account id, so the account tag is positional within
            // a service: the report e-mail when present, otherwise #1, #2, … in OMP order.
            var reportInfos = new List<(string Provider, string Email, JsonElement Limits, int Ordinal, int Siblings)>();
            var providerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var providerEmails = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var report in reports.EnumerateArray())
            {
                string provider = report.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() ?? "omp"
                    : "omp";
                if (!report.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
                    continue;

                string email = "";
                if (report.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                    && metadata.TryGetProperty("email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String)
                    email = (emailEl.GetString() ?? "").Trim();

                int ordinal = providerCounts.TryGetValue(provider, out int n) ? n + 1 : 1;
                providerCounts[provider] = ordinal;
                if (!providerEmails.TryGetValue(provider, out var emails))
                {
                    emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    providerEmails[provider] = emails;
                }
                if (!string.IsNullOrEmpty(email))
                    emails.Add(email);
                reportInfos.Add((provider, email, limits, ordinal, 0));
            }
            for (int i = 0; i < reportInfos.Count; i++)
            {
                var info = reportInfos[i];
                info.Siblings = providerCounts[info.Provider];
                reportInfos[i] = info;
            }

            // An account whose every monthly window — or every weekly window — is
            // fully eaten is dead weight: hide the whole account so it does not
            // bury live quotas. Filtering runs on every poll, so accounts come
            // back by themselves after OMP reports a reset. Accounts without any
            // monthly/weekly window are always kept.
            var visible = reportInfos.Where(keep => !IsDeadAccount(keep)).ToList();
            if (visible.Count == 0)
                visible = reportInfos;

            var extras = new List<NamedRateWindow>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            string? hottestTitle = null;
            string? hottestExtraId = null;
            RateWindow? hottest = null;

            foreach (var info in visible)
            {
                string accountTag = "";
                if (info.Siblings > 1)
                {
                    accountTag = !string.IsNullOrEmpty(info.Email) && providerEmails[info.Provider].Count > 1
                        ? EmailPrefix(info.Email)
                        : $"#{info.Ordinal}";
                }

                foreach (var limit in info.Limits.EnumerateArray())
                {
                    string limitId = limit.TryGetProperty("id", out var lid) && lid.ValueKind == JsonValueKind.String
                        ? lid.GetString() ?? "limit"
                        : "limit";
                    string id = info.Siblings > 1
                        ? $"{info.Provider}#{info.Ordinal}:{limitId}"
                        : $"{info.Provider}:{limitId}";

                    var window = BuildWindow(limit);
                    if (window is null)
                        continue;

                    string title = BuildTitle(info.Provider, limit, accountTag);
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
                    continue; // primary already carries the hottest window; avoid a duplicate bar
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

        internal static bool IsDeadAccount((string Provider, string Email, JsonElement Limits, int Ordinal, int Siblings) info)
        {
            return AllEaten(info, isMonthly: true) || AllEaten(info, isMonthly: false);
        }

        internal static bool AllEaten((string Provider, string Email, JsonElement Limits, int Ordinal, int Siblings) info, bool isMonthly)
        {
            bool any = false;
            foreach (var limit in info.Limits.EnumerateArray())
            {
                bool match = isMonthly ? IsMonthlyLimit(limit) : IsWeeklyLimit(limit);
                if (!match)
                    continue;
                any = true;
                if (LimitUsedPercent(limit) < 100)
                    return false;
            }
            return any;
        }

        internal static bool IsWeeklyLimit(JsonElement limit)
        {
            foreach (var key in new[] { "id", "label" })
            {
                if (limit.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
                    && ContainsWeeklyToken(el.GetString() ?? ""))
                    return true;
            }
            if (limit.TryGetProperty("window", out var window) && window.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "id", "label" })
                {
                    if (window.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
                        && ContainsWeeklyToken(el.GetString() ?? ""))
                        return true;
                }
            }
            if (limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                && scope.TryGetProperty("windowId", out var windowId) && windowId.ValueKind == JsonValueKind.String
                && ContainsWeeklyToken(windowId.GetString() ?? ""))
                return true;
            return false;
        }

        internal static bool ContainsWeeklyToken(string value)
        {
            string squashed = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return squashed.Contains("weekly") || squashed == "7d" || squashed == "1w"
                || squashed.Contains("perweek") || squashed == "7day" || squashed == "7days";
        }

        internal static bool IsMonthlyLimit(JsonElement limit)
        {
            foreach (var key in new[] { "id", "label" })
            {
                if (limit.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
                    && ContainsMonthlyToken(el.GetString() ?? ""))
                    return true;
            }
            if (limit.TryGetProperty("window", out var window) && window.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "id", "label" })
                {
                    if (window.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
                        && ContainsMonthlyToken(el.GetString() ?? ""))
                        return true;
                }
            }
            if (limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                && scope.TryGetProperty("windowId", out var windowId) && windowId.ValueKind == JsonValueKind.String
                && ContainsMonthlyToken(windowId.GetString() ?? ""))
                return true;
            return false;
        }

        internal static bool ContainsMonthlyToken(string value)
        {
            string squashed = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return squashed.Contains("monthly") || squashed.Contains("30d") || squashed == "1m" || squashed.Contains("permonth");
        }

        internal static double LimitUsedPercent(JsonElement limit)
        {
            if (limit.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Object)
            {
                if (amount.TryGetProperty("usedFraction", out var frac) && frac.ValueKind == JsonValueKind.Number)
                    return Math.Clamp(frac.GetDouble() * 100, 0, 100);
                if (amount.TryGetProperty("used", out var used) && used.ValueKind == JsonValueKind.Number
                    && amount.TryGetProperty("limit", out var cap) && cap.ValueKind == JsonValueKind.Number
                    && cap.GetDouble() > 0)
                    return Math.Clamp(used.GetDouble() / cap.GetDouble() * 100, 0, 100);
            }
            return 0;
        }

        internal static string BuildTitle(string provider, JsonElement limit, string accountTag)
        {
            string service = ServiceShortName(provider);
            if (!string.IsNullOrEmpty(accountTag))
                service = $"{service} {accountTag}";

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

            // "Usage (Google)" carries the qualifier that tells same-named windows apart.
            string qualifier = "";
            int paren = limitLabel.IndexOf('(');
            int parenEnd = paren >= 0 ? limitLabel.IndexOf(')', paren + 1) : -1;
            if (paren >= 0 && parenEnd > paren + 1)
                qualifier = limitLabel.Substring(paren + 1, parenEnd - paren - 1).Trim();

            string core;
            if (!string.IsNullOrEmpty(qualifier))
                core = string.IsNullOrEmpty(windowLabel) ? qualifier : $"{qualifier} · {windowLabel}";
            else if (string.IsNullOrEmpty(windowLabel))
                core = string.IsNullOrEmpty(limitLabel) ? "Usage" : limitLabel;
            else if (string.IsNullOrEmpty(limitLabel)
                || limitLabel.Contains(windowLabel, StringComparison.OrdinalIgnoreCase)
                || IsSameName(limitLabel, service))
                core = windowLabel;
            else
                core = $"{limitLabel} · {windowLabel}";

            return $"{service} · {core}{amountSuffix}";
        }

        private static bool IsSameName(string label, string service)
        {
            static string Squash(string value)
                => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return Squash(label) == Squash(service);
        }

        private static string EmailPrefix(string email)
        {
            int at = email.IndexOf('@');
            string local = (at > 0 ? email.Substring(0, at) : email).Trim();
            return string.IsNullOrEmpty(local) ? email : local;
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
