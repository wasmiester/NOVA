using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Docs.v1;
using Google.Apis.Docs.v1.Data;
using Google.Apis.Services;

namespace Nova;

internal sealed class DocsClient
{
    private readonly DocsService _service;

    public DocsClient(UserCredential credential)
    {
        _service = new DocsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Nova",
        });
    }

    public async Task<string> ReadAsync(string documentId, CancellationToken cancellationToken)
    {
        Document doc = await GoogleRateLimitRetry.WithRetryAsync(() => _service.Documents.Get(documentId).ExecuteAsync(cancellationToken), cancellationToken);
        string text = ExtractText(doc);
        return string.IsNullOrWhiteSpace(text) ? $"\"{doc.Title}\" is empty." : $"\"{doc.Title}\":\n{text}";
    }

    // Docs.Create only sets the title - actual body content always needs a
    // separate BatchUpdate (InsertTextRequest), even for a brand new doc.
    public async Task<string> CreateAsync(string title, string? content, CancellationToken cancellationToken)
    {
        Document created = await GoogleRateLimitRetry.WithRetryAsync(() => _service.Documents.Create(new Document { Title = title }).ExecuteAsync(cancellationToken), cancellationToken);

        if (!string.IsNullOrEmpty(content))
        {
            await InsertAtEndAsync(created.DocumentId, content, cancellationToken);
        }

        return $"Created \"{title}\" (id: {created.DocumentId}).";
    }

    public async Task<string> AppendAsync(string documentId, string text, CancellationToken cancellationToken)
    {
        await InsertAtEndAsync(documentId, text, cancellationToken);
        return "Appended text to the document.";
    }

    // Find-and-replace by matched text, not by character index -
    // deliberately, even though the Docs API also supports index-based
    // insert/delete. Indices shift as content changes and are easy to get
    // subtly wrong (especially reasoning over a document read back as
    // plain extracted text, not the API's own structural-element tree);
    // matching on the actual text Claude already read is far more robust,
    // and it's what ReplaceAllTextRequest is built for.
    public async Task<string> ReplaceTextAsync(string documentId, string findText, string replaceText, bool matchCase, CancellationToken cancellationToken)
    {
        var request = new BatchUpdateDocumentRequest
        {
            Requests =
            [
                new Request
                {
                    ReplaceAllText = new ReplaceAllTextRequest
                    {
                        ContainsText = new SubstringMatchCriteria { Text = findText, MatchCase = matchCase },
                        ReplaceText = replaceText,
                    },
                },
            ],
        };

        BatchUpdateDocumentResponse response = await GoogleRateLimitRetry.WithRetryAsync(() => _service.Documents.BatchUpdate(request, documentId).ExecuteAsync(cancellationToken), cancellationToken);
        int? occurrences = response.Replies?.FirstOrDefault()?.ReplaceAllText?.OccurrencesChanged;
        return GoogleReplaceTextResult.Describe(occurrences, findText, replaceText, "doc");
    }

    private async Task InsertAtEndAsync(string documentId, string text, CancellationToken cancellationToken)
    {
        var request = new BatchUpdateDocumentRequest
        {
            // No SegmentId on EndOfSegmentLocation means the end of the
            // document body itself, not a header/footer/footnote segment.
            Requests = [new Request { InsertText = new InsertTextRequest { Text = text, EndOfSegmentLocation = new EndOfSegmentLocation() } }],
        };
        await GoogleRateLimitRetry.WithRetryAsync(() => _service.Documents.BatchUpdate(request, documentId).ExecuteAsync(cancellationToken), cancellationToken);
    }

    // A Doc's body is a tree of StructuralElements; only Paragraph elements
    // carry visible text, as a list of ParagraphElements whose TextRun.Content
    // holds the actual characters - tables/images/etc. are skipped rather
    // than attempted, matching read_file/browser_read's "best-effort text
    // extraction" scope elsewhere in this codebase.
    private static string ExtractText(Document doc)
    {
        var sb = new StringBuilder();
        IList<StructuralElement>? content = doc.Body?.Content;
        if (content is null)
        {
            return "";
        }

        foreach (StructuralElement element in content)
        {
            IList<ParagraphElement>? paragraphElements = element.Paragraph?.Elements;
            if (paragraphElements is null)
            {
                continue;
            }

            foreach (ParagraphElement pe in paragraphElements)
            {
                if (pe.TextRun?.Content is { } textContent)
                {
                    sb.Append(textContent);
                }
            }
        }

        return sb.ToString();
    }
}
