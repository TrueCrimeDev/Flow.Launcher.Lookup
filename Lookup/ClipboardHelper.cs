using System;
using System.Threading;
using System.Windows;
using Flow.Launcher.Plugin;

namespace Lookup;

/// <summary>
/// Clipboard writes that survive contention. Flow Launcher ≤ 2.1.3 copies text via
/// SetDataObject + Clipboard.Flush(), and Flush throws CLIPBRD_E_CANT_OPEN when a
/// clipboard listener (clipboard history, sync tools) opens the clipboard the moment
/// its content changes — surfacing as a spurious "Failed to copy" toast even though
/// the user did nothing wrong. This mirrors the post-2.1.3 upstream fix: SetText
/// (no flush) with brief retries, handing off to the host API only as a last resort.
/// </summary>
internal static class ClipboardHelper
{
    /// <summary>Must run on the UI (STA) thread — result and context-menu actions do.
    /// Worst case blocks ~300 ms before falling back to the host's async copy.</summary>
    public static void Copy(string text, IPublicAPI api)
    {
        if (string.IsNullOrEmpty(text)) return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch
            {
                Thread.Sleep(60); // clipboard briefly held by a listener; try again
            }
        }

        // Still locked after ~300 ms: let the host retry off-thread and, if even
        // that fails, surface its own "Failed to copy" message.
        api.CopyToClipboard(text);
    }
}
