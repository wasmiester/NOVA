using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Docs.v1;
using Google.Apis.Drive.v3;
using Google.Apis.Gmail.v1;
using Google.Apis.Sheets.v4;
using Google.Apis.Slides.v1;
using Google.Apis.Util.Store;

namespace Nova;

// OAuth2 "installed app" flow: opens the user's default browser to Google's
// consent page the first time, listens on a local loopback port for the
// redirect, then caches the resulting refresh token to disk - every
// subsequent run reuses that cached token silently, no browser involved,
// until the user revokes access. Needs a Google Cloud project with the
// Gmail, Calendar, Docs, Drive, Sheets, and Slides APIs enabled and an
// OAuth Client ID (type "Desktop app") - see secrets/.env.example for the
// exact steps. Adding a new API's scope here means an *existing* cached
// token (from before that scope existed) no longer covers everything
// requested - GoogleWebAuthorizationBroker handles that itself by
// re-prompting for consent once, covering the full new scope set, same
// flow as the very first connect.
internal static class GoogleAuth
{
    private static readonly string[] Scopes =
    [
        GmailService.Scope.GmailSend,
        GmailService.Scope.GmailReadonly,
        CalendarService.Scope.Calendar,
        DocsService.Scope.Documents,
        // Read access to anything the user can see (needed to find/read a
        // doc Nova didn't create herself), but *write* access only to
        // files Nova creates/uploads - not blanket write/delete over the
        // user's entire existing Drive. Least-privilege split Drive
        // itself offers; Docs' own API has no equivalent narrower scope,
        // so Documents above is broader by necessity.
        DriveService.Scope.DriveReadonly,
        DriveService.Scope.DriveFile,
        // Sheets/Slides have the same "one broad read+write scope, no
        // narrower split" shape Docs does - DriveFile above still limits
        // *creation* to files Nova makes herself; these two just cover
        // actually editing a sheet/presentation's content once it exists
        // (one she made, or one already shared with the user and opened
        // by ID - same trust level Docs' own scope already grants there).
        SheetsService.Scope.Spreadsheets,
        SlidesService.Scope.Presentations,
    ];

    public static async Task<UserCredential> AuthorizeAsync(string clientId, string clientSecret, string tokenStoreDir, CancellationToken cancellationToken)
    {
        var secrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret };
        return await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            Scopes,
            "user",
            cancellationToken,
            new FileDataStore(tokenStoreDir, fullPath: true));
    }
}
