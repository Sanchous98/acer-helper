namespace AcerHelper;

// Command-line switches, centralised so the launcher (Program), the autostart registration (Autostart) and the
// arg parsing (App) can't drift apart — a mismatch between what gets registered and what gets parsed silently
// breaks autostart (which is exactly what bit us before).
internal static class AppArgs
{
    public const string Startup = "--startup";   // full app, started minimised to the tray (used by autostart)
}
