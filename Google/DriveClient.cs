using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
// System.IO.File already covers plain local-file existence/read here -
// aliased to avoid the collision, same pattern GmailClient.cs uses for its
// own Message type collision with System.Windows.Forms.
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace Nova;

internal sealed class DriveClient
{
    private readonly DriveService _service;

    public DriveClient(UserCredential credential)
    {
        _service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Nova",
        });
    }

    // Drive's own query syntax (e.g. "name contains 'resume'") - passed
    // straight through, same reasoning as GmailClient.SearchAsync using
    // Gmail's own search syntax untranslated.
    public async Task<string> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        FilesResource.ListRequest request = _service.Files.List();
        request.Q = query;
        request.PageSize = maxResults;
        request.Fields = "files(id, name, mimeType, webViewLink, modifiedTime)";

        Google.Apis.Drive.v3.Data.FileList result = await request.ExecuteAsync(cancellationToken);
        if (result.Files is null || result.Files.Count == 0)
        {
            return "No matching files found.";
        }

        var sb = new StringBuilder();
        foreach (DriveFile file in result.Files)
        {
            sb.AppendLine($"{file.Name} ({file.MimeType}) - id: {file.Id}");
        }

        return sb.ToString();
    }

    // Uses the DriveFile OAuth scope (see GoogleAuth) - Nova can only write
    // files she creates this way, never overwrite/delete something already
    // in the user's Drive that she didn't upload herself.
    public async Task<string> UploadAsync(string localFilePath, string? driveFileName, CancellationToken cancellationToken)
    {
        if (!File.Exists(localFilePath))
        {
            return $"File not found: {localFilePath}";
        }

        var metadata = new DriveFile { Name = driveFileName ?? Path.GetFileName(localFilePath) };
        await using FileStream stream = File.OpenRead(localFilePath);
        FilesResource.CreateMediaUpload request = _service.Files.Create(metadata, stream, GuessMimeType(localFilePath));
        request.Fields = "id, name, webViewLink";
        IUploadProgress progress = await request.UploadAsync(cancellationToken);
        if (progress.Status != UploadStatus.Completed)
        {
            return $"Upload failed: {progress.Exception?.Message ?? "unknown error"}";
        }

        return $"Uploaded to Drive as \"{request.ResponseBody?.Name}\" ({request.ResponseBody?.WebViewLink}).";
    }

    private static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream",
    };
}
