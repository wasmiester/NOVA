using System;

namespace Nova;

// Every console line reporting what Nova is currently doing or reacting to
// goes through here instead of a bare Console.WriteLine, so a stalled task
// is actually diagnosable - "stuck" otherwise looks identical whether she's
// been quiet for 200ms or 20 minutes, since nothing in the console said
// when anything happened.
internal static class StatusLog
{
    public static void WriteLine(string message)
    {
        int i = 0;
        while (i < message.Length && message[i] == '\n')
        {
            i++;
        }

        if (i > 0)
        {
            Console.Write(message[..i]);
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message[i..]}");
    }

    public static void Write(string message) => Console.Write($"[{DateTime.Now:HH:mm:ss}] {message}");
}
