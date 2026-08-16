namespace Nova;

// IsPending marks a Gate 1/Gate 2 "should I go ahead?" ask - the 3 skins
// render these with the source mockup's distinct "awaiting go-ahead" look
// instead of a normal Nova line.
internal readonly record struct TranscriptEntry(bool IsUser, string Text, bool IsPending = false);
