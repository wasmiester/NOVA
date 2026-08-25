using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Nova;

// Google's own guidance for a 429 is to back off and retry, not treat it as
// a hard failure - previously any rate-limit hit surfaced straight to
// Claude as a generic "Tool error," indistinguishable from a real failure,
// which led to reformulating the query instead of just trying again
// shortly after. Retries transparently - Claude never needs to know a rate
// limit happened unless every attempt still fails.
//
// Shared by every Google client. Confirmed live as a real gap: this used
// to exist only in GmailClient (built after a real observed bug), leaving
// Calendar/Docs/Drive/Sheets/Slides exposed to the identical failure mode
// on the same kind of burst traffic (e.g. writing many spreadsheet rows or
// building a multi-slide deck back to back), and silently degrading
// CalendarWatcher - a transient 429 during a reminder poll was swallowed
// by its own empty catch with nothing underneath it to retry first.
internal static class GoogleRateLimitRetry
{
    public static async Task<T> WithRetryAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        TimeSpan delay = TimeSpan.FromMilliseconds(500);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Google.GoogleApiException ex) when (IsRateLimit(ex) && attempt < maxAttempts)
            {
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }

    // Some Google APIs (including Gmail, historically) surface the same
    // quota failure as 403 with a rateLimitExceeded/userRateLimitExceeded
    // reason instead of a plain 429 - the original GmailClient check only
    // ever looked for 429, a gap flagged during review but not yet
    // confirmed against this app's own live traffic. Cheap and safe to
    // handle both regardless.
    private static bool IsRateLimit(Google.GoogleApiException ex)
    {
        if (ex.HttpStatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        return ex.HttpStatusCode == HttpStatusCode.Forbidden &&
            (ex.Error?.Errors?.Any(e => e.Reason is "rateLimitExceeded" or "userRateLimitExceeded") ?? false);
    }
}
