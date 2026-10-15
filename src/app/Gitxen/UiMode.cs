// SPDX-License-Identifier: GPL-3.0-or-later
namespace Gitxen;

/// <summary>
///  The level of UI detail the shell presents.
/// </summary>
public enum UiMode
{
    /// <summary>Only the essentials: a clean, minimal commit list and a few core actions.</summary>
    Simple,

    /// <summary>The fuller view, closer to what the WinForms app exposes.</summary>
    Advanced,

    /// <summary>
    ///  Distraction-free: no toolbar, no column headers, no chrome beyond the commits themselves.
    ///  Ctrl+Shift+Z turns it on and off; Escape leaves it, as does the hint it leaves floating in the
    ///  corner. The other two are Ctrl+Shift+1 and Ctrl+Shift+2 — there is no picker.
    /// </summary>
    Zen
}
