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

        return string.Join("\n", results.Select(m => $"From: {m.From} | Date: {m.Date} | Subject: {m.Subject} | {m.Snippet}"));
    }

    // Used by GmailWatcher to poll for new mail - kept separate from
    // SearchAsync since the watcher wants structured data, not a
    // pre-formatted string meant for Claude to relay.
    public async Task<List<EmailSummary>> FetchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        UsersResource.MessagesResource.ListRequest listRequest = _service.Users.Messages.List(UserId);
        listRequest.Q = query;
        listRequest.MaxResults = maxResults;
        ListMessagesResponse list = await listRequest.ExecuteAsync(cancellationToken);
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
        // down to roughly one round-trip's worth of latency.
        Message[] fullMessages = await Task.WhenAll(
            list.Messages.Select(stub => _service.Users.Messages.Get(UserId, stub.Id).ExecuteAsync(cancellationToken)));

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
