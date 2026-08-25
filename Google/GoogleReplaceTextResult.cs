namespace Nova;

// Shared by DocsClient.ReplaceTextAsync and SlidesClient.ReplaceTextAsync -
// both build a near-identical BatchUpdate...Request with a
// ReplaceAllTextRequest and report back the same shape of result, but can't
// share the request-building code itself since Docs and Slides each
// generate their own distinct Request/ReplaceAllTextRequest types (Google.
// Apis.Docs.v1.Data vs Google.Apis.Slides.v1.Data) - same name, genuinely
// different types. This covers the part that actually was identical: the
// "did it find anything" response text, previously duplicated verbatim in
// both files.
internal static class GoogleReplaceTextResult
{
    public static string Describe(int? occurrences, string findText, string replaceText, string documentKind) =>
        occurrences is null or 0
            ? $"No occurrences of \"{findText}\" found - nothing was changed. Re-read the {documentKind} if you're not sure of the exact text."
            : $"Replaced {occurrences} occurrence(s) of \"{findText}\" with \"{replaceText}\".";
}
