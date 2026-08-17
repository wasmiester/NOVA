using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
// UseWindowsForms=true adds a project-wide implicit `using
// System.Windows.Forms;`, which collides with Gmail's own Message type.
using Message = Google.Apis.Gmail.v1.Data.Message;

namespace Nova;

internal sealed record EmailSummary(string Id, string From, string Subject, string Snippet, string Date);

internal sealed class GmailClient
{
    private const string UserId = "me";
    private readonly GmailService _service;

    // Confirmed live via errors.log: fetching every matched message
    // concurrently (see FetchAsync below) tripped Gmail's own "too many
    // concurrent requests for user" quota (429) repeatedly during a task
    // making several broad searches back to back - worse than the
    // sequential latency it was meant to fix, since an unexplained tool
    // error tends to push Claude toward reformulating the query (the
    // wrong response to a rate limit) rather than just waiting. A small
    // cap keeps most of the concurrency win without tripping the quota
    // that hard - 4 is a deliberate, conservative guess, not measured
    // against Gmail's actual undocumented limit.
    private static readonly SemaphoreSlim GetConcurrencyLimit = new(4);

    public GmailClient(UserCredential credential)
    {
        _service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Nova",
        });
    }

    public async Task<string> SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        var message = new Message { Raw = BuildRawMessage(to, subject, body) };
        await _service.Users.Messages.Send(message, UserId).ExecuteAsync(cancellationToken);
        return $"Email sent to {to}.";
    }

    // Gmail's own search syntax (e.g. "is:unread", "from:someone@example.com",
    // "subject:invoice") - passed straight through rather than reinterpreted.
    public async Task<string> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        List<EmailSummary> results = await FetchAsync(query, maxResults, cancellationToken);
        if (results.Count == 0)
        {
            return "No matching emails found.";
        }

        // id included so a follow-up read_email call (for the rare case a
        // date/detail is buried past the snippet) has something to target -
        // Claude doesn't need to read this part aloud, just use it.
        return string.Join("\n", results.Select(m => $"From: {m.From} | Date: {m.Date} | Subject: {m.Subject} | {m.Snippet} | id: {m.Id}"));
    }

    // Full plain-text body of one already-identified message - the
    // deliberate follow-up to a broad SearchAsync scan, for the rare case
    // something needed (an exact time, a reference number) is buried past
    // the ~100-200 char snippet. Gmail messages are MIME under the hood,
    // usually multipart (text/plain and text/html as alternatives,
    // sometimes nested further, sometimes with attachments mixed in) and
    // base64url-encoded - GetPlainTextBody below walks that structure for
    // the actual readable content instead of Claude ever seeing raw MIME.
    public async Task<string> ReadEmailAsync(string messageId, CancellationToken cancellationToken)
    {
        Message full = await WithRateLimitRetryAsync(
            () => _service.Users.Messages.Get(UserId, messageId).ExecuteAsync(cancellationToken), cancellationToken);
        string subject = HeaderValue(full, "Subject") ?? "(no subject)";
        string from = HeaderValue(full, "From") ?? "(unknown sender)";
        string date = HeaderValue(full, "Date") ?? "(unknown date)";
        string body = GetPlainTextBody(full.Payload) ?? "(no readable body content found)";
        return $"From: {from}\nDate: {date}\nSubject: {subject}\n\n{body}";
    }

    // Prefers text/plain; falls back to text/html with tags stripped (a
    // real plain-text part doesn't always exist - some senders, especially
    // marketing/ATS platforms, send HTML-only). Recurses since a
    // multipart/alternative part can itself be nested inside a further
    // multipart/mixed wrapper (e.g. when an attachment is also present).
    private static string? GetPlainTextBody(MessagePart? part)
    {
        if (part is null)
        {
            return null;
        }

        if (part.MimeType == "text/plain" && part.Body?.Data is { } plainData)
        {
            return DecodeBase64Url(plainData);
        }

        if (part.Parts is { Count: > 0 })
        {
            // One pass preferring text/plain among the children, since a
            // multipart/alternative wrapper lists both versions of the same
            // content side by side - only fall back to HTML if no sibling
            // at this level was plain text.
            foreach (MessagePart child in part.Parts)
            {
                if (child.MimeType == "text/plain" && child.Body?.Data is { } childPlainData)
                {
                    return DecodeBase64Url(childPlainData);
                }
            }

            foreach (MessagePart child in part.Parts)
            {
                if (GetPlainTextBody(child) is { } nested)
                {
                    return nested;
                }
            }
        }

        if (part.MimeType == "text/html" && part.Body?.Data is { } htmlData)
        {
            return StripHtml(DecodeBase64Url(htmlData));
        }

        return null;
    }

    private static string DecodeBase64Url(string data)
    {
        string padded = data.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    // Not a real HTML parser - good enough for turning a marketing/ATS
    // email's markup into readable text (tags stripped, entities decoded),
    // not for anything that needs to preserve structure.
    private static string StripHtml(string html)
    {
        string noTags = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        string decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return System.Text.RegularExpressions.Regex.Replace(decoded, @"[ \t]+", " ").Trim();
    }

    // Used by GmailWatcher to poll for new mail - kept separate from
    // SearchAsync since the watcher wants structured data, not a
    // pre-formatted string meant for Claude to relay.
    public async Task<List<EmailSummary>> FetchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        UsersResource.MessagesResource.ListRequest listRequest = _service.Users.Messages.List(UserId);
        listRequest.Q = query;
        listRequest.MaxResults = maxResults;
        ListMessagesResponse list = await WithRateLimitRetryAsync(() => listRequest.ExecuteAsync(cancellationToken), cancellationToken);
        if (list.Messages is null || list.Messages.Count == 0)
        {
            return [];
        }

        // Each Get is an independent, read-only fetch of a different
        // message - previously awaited one at a time in the loop below,
        // meaning a broad query matching close to maxResults messages paid
        // for that many sequential Gmail API round-trips in a row. Across a
        // task making several broad searches back to back (exactly the
        // pattern a multi-company job-tracker lookup produces), that adds
        // up to real, visible wall-clock time with no intermediate status
        // update in between - fetching them concurrently instead cuts that
        // down to roughly one round-trip's worth of latency. Capped by
        // GetConcurrencyLimit and retried on 429 - see both for why.
        Message[] fullMessages = await Task.WhenAll(list.Messages.Select(async stub =>
        {
            await GetConcurrencyLimit.WaitAsync(cancellationToken);
            try
            {
                return await WithRateLimitRetryAsync(
                    () => _service.Users.Messages.Get(UserId, stub.Id).ExecuteAsync(cancellationToken), cancellationToken);
            }
            finally
            {
                GetConcurrencyLimit.Release();
            }
        }));

        var results = new List<EmailSummary>(fullMessages.Length);
        foreach (Message full in fullMessages)
        {
            string subject = HeaderValue(full, "Subject") ?? "(no subject)";
            string from = HeaderValue(full, "From") ?? "(unknown sender)";
            // The Date header was sitting right here unused - Gmail's API
            // always returns it on the same full-message fetch already made
            // for Subject/From above, it just wasn't being read out. Found
            // live: a job-tracker task needed "date applied" and had no way
            // to get it from search_email's output at all.
            string date = HeaderValue(full, "Date") ?? "(unknown date)";
            results.Add(new EmailSummary(full.Id, from, subject, full.Snippet ?? "", date));
        }

        return results;
    }

    // Google's own guidance for a 429 is to back off and retry, not treat
    // it as a hard failure - previously any rate-limit hit surfaced
    // straight to Claude as a generic "Tool error," indistinguishable from
    // a real failure, which led to reformulating the query (the wrong
    // response to a rate limit, and part of why near-duplicate searches
    // kept showing up seconds apart) instead of just trying again shortly
    // after. Retries transparently - Claude never needs to know a rate
    // limit happened unless every attempt still fails.
    private static async Task<T> WithRateLimitRetryAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        TimeSpan delay = TimeSpan.FromMilliseconds(500);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt < maxAttempts)
            {
                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }

    private static string? HeaderValue(Message message, string name) =>
        message.Payload?.Headers?.FirstOrDefault(h => h.Name == name)?.Value;

    // Plain ASCII subjects/bodies only for v1 - no MIME encoded-word support
    // for non-ASCII subjects, matching the "baseline" scope of this feature.
    private static string BuildRawMessage(string to, string subject, string body)
    {
        string mime = $"To: {to}\r\nSubject: {subject}\r\nContent-Type: text/plain; charset=UTF-8\r\n\r\n{body}";
        byte[] bytes = Encoding.UTF8.GetBytes(mime);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
