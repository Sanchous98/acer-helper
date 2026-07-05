namespace AcerHelper;

// Linux has no standalone watch mode yet: the special keys come through evdev (AcerHotkeys.Linux) which the
// running app reads. A logon watcher would need its own evdev loop + launch; not wired. No-op so --watch
// compiles and degrades gracefully on the portable build.
internal static partial class Watcher
{
    public static partial void Run()
        => Console.Error.WriteLine("--watch is not supported on this platform.");
}
