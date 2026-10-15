// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;

// The shared types stay internal: they are Gitxen's own, not an API. The window and gx may use them.
[assembly: InternalsVisibleTo("Gitxen")]
[assembly: InternalsVisibleTo("gx")]
