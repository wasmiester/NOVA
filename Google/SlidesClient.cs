using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Slides.v1;
using Google.Apis.Slides.v1.Data;

namespace Nova;

internal sealed class SlidesClient
{
    private readonly SlidesService _service;

    public SlidesClient(UserCredential credential)
    {
        _service = new SlidesService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Nova",
        });
    }

    public async Task<string> ReadAsync(string presentationId, CancellationToken cancellationToken)
    {
        Presentation presentation = await _service.Presentations.Get(presentationId).ExecuteAsync(cancellationToken);
        IList<Page>? slides = presentation.Slides;
        if (slides is null || slides.Count == 0)
        {
            return $"\"{presentation.Title}\" has no slides.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"\"{presentation.Title}\" ({slides.Count} slide(s)):");
        for (int i = 0; i < slides.Count; i++)
        {
            string text = ExtractText(slides[i]);
            sb.AppendLine($"Slide {i + 1}: {(string.IsNullOrWhiteSpace(text) ? "(no text)" : text)}");
        }

        return sb.ToString();
    }

    // Create only sets the title, same as DocsClient/SheetsClient - actual
    // slide content always needs append_slide afterward, since a
    // presentation has no equivalent of a doc's body to write straight
    // into at creation time.
    public async Task<string> CreateAsync(string title, CancellationToken cancellationToken)
    {
        Presentation created = await _service.Presentations.Create(new Presentation { Title = title }).ExecuteAsync(cancellationToken);
        return $"Created \"{title}\" (id: {created.PresentationId}).";
    }

    // Adds one new slide using the built-in TITLE_AND_BODY layout, then
    // fills in its two placeholder shapes - a slide's placeholder object
    // IDs don't exist until the slide itself does, so this is necessarily
    // two round trips: create the slide, then look up which shape is the
    // title/body placeholder before text can be inserted into either.
    public async Task<string> AppendSlideAsync(string presentationId, string title, string? body, CancellationToken cancellationToken)
    {
        var createRequest = new BatchUpdatePresentationRequest
        {
            Requests =
            [
                new Request
                {
                    CreateSlide = new CreateSlideRequest
                    {
                        SlideLayoutReference = new LayoutReference { PredefinedLayout = "TITLE_AND_BODY" },
                    },
                },
            ],
        };
        BatchUpdatePresentationResponse createResponse = await _service.Presentations.BatchUpdate(createRequest, presentationId).ExecuteAsync(cancellationToken);
        // Safe-navigated, same as ReplaceTextAsync below and DocsClient -
        // a batch reply missing its matching entry (partial API failure,
        // quota edge case) should surface as a clear error, not an NRE.
        string? newSlideId = createResponse.Replies?.FirstOrDefault()?.CreateSlide?.ObjectId;
        if (newSlideId is null)
        {
            return "The presentation API didn't confirm the new slide was created - nothing was added.";
        }

        Page slide = await _service.Presentations.Pages.Get(presentationId, newSlideId).ExecuteAsync(cancellationToken);
        string? titlePlaceholderId = FindPlaceholderId(slide, "TITLE");
        string? bodyPlaceholderId = FindPlaceholderId(slide, "BODY");

        var textRequests = new List<Request>();
        if (titlePlaceholderId is not null)
        {
            textRequests.Add(new Request { InsertText = new InsertTextRequest { ObjectId = titlePlaceholderId, Text = title } });
        }

        if (bodyPlaceholderId is not null && !string.IsNullOrEmpty(body))
        {
            textRequests.Add(new Request { InsertText = new InsertTextRequest { ObjectId = bodyPlaceholderId, Text = body } });
        }

        if (textRequests.Count > 0)
        {
            await _service.Presentations.BatchUpdate(new BatchUpdatePresentationRequest { Requests = textRequests }, presentationId).ExecuteAsync(cancellationToken);
        }

        // Report what actually happened rather than a blanket success -
        // an unusual/custom theme without the expected TITLE/BODY
        // placeholder shapes would otherwise silently create a blank slide
        // while claiming the requested title/body text was added.
        if (titlePlaceholderId is null)
        {
            return $"Added a new slide, but couldn't find its title placeholder - \"{title}\" was NOT actually set. " +
                   "The presentation's theme may not use the standard title-and-body layout.";
        }

        if (bodyPlaceholderId is null && !string.IsNullOrEmpty(body))
        {
            return $"Added a new slide titled \"{title}\", but couldn't find its body placeholder - the body text was NOT actually set.";
        }

        return $"Added a new slide (\"{title}\") to the presentation.";
    }

    // Same find-and-replace-by-text reasoning as DocsClient.ReplaceTextAsync -
    // matches across every slide in the presentation, not a single one.
    public async Task<string> ReplaceTextAsync(string presentationId, string findText, string replaceText, bool matchCase, CancellationToken cancellationToken)
    {
        var request = new BatchUpdatePresentationRequest
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

        BatchUpdatePresentationResponse response = await _service.Presentations.BatchUpdate(request, presentationId).ExecuteAsync(cancellationToken);
        int? occurrences = response.Replies?.FirstOrDefault()?.ReplaceAllText?.OccurrencesChanged;
        return occurrences is null or 0
            ? $"No occurrences of \"{findText}\" found - nothing was changed. Re-read the presentation if you're not sure of the exact text."
            : $"Replaced {occurrences} occurrence(s) of \"{findText}\" with \"{replaceText}\".";
    }

    private static string? FindPlaceholderId(Page slide, string placeholderType) =>
        slide.PageElements?.FirstOrDefault(el => el.Shape?.Placeholder?.Type == placeholderType)?.ObjectId;

    // A slide's text lives in its shapes' TextElements, as TextRun.Content -
    // same "walk the structural tree, only extract TextRun content" shape
    // as DocsClient.ExtractText, just one level deeper (page -> element ->
    // shape -> text) since a slide is a collection of positioned shapes,
    // not one flowing body.
    private static string ExtractText(Page slide)
    {
        if (slide.PageElements is null)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (PageElement element in slide.PageElements)
        {
            IList<TextElement>? textElements = element.Shape?.Text?.TextElements;
            if (textElements is null)
            {
                continue;
            }

            foreach (TextElement te in textElements)
            {
                if (te.TextRun?.Content is { } content)
                {
                    sb.Append(content.Replace("\n", " ").Trim()).Append(' ');
                }
            }
        }

        return sb.ToString().Trim();
    }
}
