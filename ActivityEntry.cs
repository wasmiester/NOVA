namespace Nova;

// InProgress marks the single most recent entry while its tool call is
// still running - the overlay's activity feed shows this one with the
// animated "..." suffix and every earlier entry as settled/static. See
// NovaAssistant.RecordActivity for how at most one entry is ever
// InProgress at a time.
internal readonly record struct ActivityEntry(string Text, bool InProgress);
