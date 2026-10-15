// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Gitxen;

internal static class AssemblyResolveFallback
{
    [ModuleInitializer]
    public static void Register()
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
    }
}
