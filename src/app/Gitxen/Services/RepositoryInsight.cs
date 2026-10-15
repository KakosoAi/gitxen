// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gitxen.Services;

/// <summary>A technology a repository uses, as a small labelled badge.</summary>
/// <param name="Label">What the badge says: two or three characters, such as "C#" or "TS".</param>
/// <param name="Name">The technology's name, for the tooltip.</param>
/// <param name="Version">Its version as the repository declares it, or empty when it does not say.</param>
/// <param name="Source">The file it was read from, relative to the repository.</param>
/// <param name="Color">The badge colour, as six hex digits.</param>
public sealed record Technology(string Label, string Name, string Version, string Source, string Color)
{
    public string Description => Version.Length > 0 ? $"{Name} {Version}  ·  {Source}" : $"{Name}  ·  {Source}";
}

/// <summary>How a run command is started in its terminal.</summary>
public enum RunShell
{
    PowerShell,
    Cmd,
    Bash
}

/// <summary>Something in a repository that can be run: a script, a build, a test run.</summary>
/// <param name="Group">What it belongs to, such as "npm scripts" or ".NET"; the menu is grouped by it.</param>
/// <param name="Title">What the menu says.</param>
/// <param name="Command">The command line, run in the repository's folder.</param>
public sealed record RunCommand(string Group, string Title, string Command, RunShell Shell);

/// <summary>What a repository is made with, and what can be run in it.</summary>
public sealed record RepositoryProfile(IReadOnlyList<Technology> Technologies, IReadOnlyList<RunCommand> Commands)
{
    public static RepositoryProfile Empty { get; } = new([], []);
}

/// <summary>
///  Reads a repository's files to see what it is built with and what can be run in it.
/// </summary>
/// <remarks>
///  <para>
///   Files only — no git, no network, no running anything — and bounded: a few levels deep, skipping
///   the folders where dependencies and build output live, and giving up after a few hundred folders.
///   What it finds is what the repository declares (a target framework, an engines field, a go
///   directive), not what happens to be installed.
///  </para>
///  <para>
///   Remembered per folder for as long as the app runs; <see cref="Forget"/> reads it again.
///  </para>
/// </remarks>
public static partial class RepositoryInsight
{
    private const int MaxDepth = 3;
    private const int MaxFolders = 400;
    private const int MaxScripts = 40;

    /// <summary>Folders never worth looking inside: dependencies, build output, tool state.</summary>
    private static readonly HashSet<string> _skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "packages", "artifacts", "dist", "out", "target", "vendor",
        ".venv", "venv", "__pycache__", ".idea", ".vs", ".vscode", ".gradle", ".dart_tool", "Pods", "coverage"
    };

    /// <summary>Where script files are looked for, besides the root.</summary>
    private static readonly string[] _scriptFolders = ["scripts", "script", "build", "eng", "tools", "setup", "ci"];

    private static readonly ConcurrentDictionary<string, RepositoryProfile> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The repository's profile, read once and then remembered. Safe to call from any thread.</summary>
    public static RepositoryProfile Read(string root)
    {
        if (_cache.TryGetValue(root, out RepositoryProfile? known))
        {
            return known;
        }

        RepositoryProfile profile;

        try
        {
            profile = Build(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            profile = RepositoryProfile.Empty;
        }

        _cache[root] = profile;
        return profile;
    }

    /// <summary>Forgets a repository's profile, so the next <see cref="Read"/> looks again.</summary>
    public static void Forget(string root) => _cache.TryRemove(root, out _);

    private static RepositoryProfile Build(string root)
    {
        if (!Directory.Exists(root))
        {
            return RepositoryProfile.Empty;
        }

        List<string> files = ListFiles(root);
        List<Technology> tech = [];
        List<RunCommand> run = [];

        string? Rel(string name) => files.FirstOrDefault(file => string.Equals(file, name, StringComparison.OrdinalIgnoreCase));
        IEnumerable<string> Named(string fileName) => files.Where(file => string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase));
        IEnumerable<string> Ending(string extension) => files.Where(file => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        string Text(string relative) => File.ReadAllText(Path.Combine(root, relative));

        DotNet(root, files, Rel, Ending, Text, tech, run);
        Node(Rel, Text, tech, run);
        Python(Rel, Text, tech, run);
        Others(Rel, Named, Ending, Text, tech, run);
        Scripts(files, run);

        // One badge per name, the first found winning: a monorepo lists .NET once, not per project.
        List<Technology> distinct = [.. tech.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.First())];
        return new RepositoryProfile(distinct, run);
    }

    // ---- .NET ------------------------------------------------------------------------------------

    private static void DotNet(
        string root,
        List<string> files,
        Func<string, string?> rel,
        Func<string, IEnumerable<string>> ending,
        Func<string, string> text,
        List<Technology> tech,
        List<RunCommand> run)
    {
        List<string> projects = [.. ending(".csproj"), .. ending(".fsproj"), .. ending(".vbproj")];
        string? solution = files.FirstOrDefault(file => !file.Contains('\\') && (file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)));

        if (projects.Count == 0 && solution is null)
        {
            return;
        }

        string framework = "";
        string source = solution ?? projects.FirstOrDefault() ?? "";
        bool web = false, winui = false, wpf = false, winforms = false, maui = false, blazor = false;

        foreach (string project in projects.Take(40))
        {
            string content = text(project);
            Match target = TargetFramework().Match(content);

            if (framework.Length == 0 && target.Success)
            {
                framework = target.Groups[1].Value.Split(';')[0].Trim();
                source = project;
            }

            web |= content.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase);
            blazor |= content.Contains("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase) || content.Contains("Microsoft.AspNetCore.Components", StringComparison.OrdinalIgnoreCase);
            winui |= content.Contains("<UseWinUI>true", StringComparison.OrdinalIgnoreCase);
            wpf |= content.Contains("<UseWPF>true", StringComparison.OrdinalIgnoreCase);
            winforms |= content.Contains("<UseWindowsForms>true", StringComparison.OrdinalIgnoreCase);
            maui |= content.Contains("<UseMaui>true", StringComparison.OrdinalIgnoreCase);
        }

        string language = projects.Any(project => project.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)) ? "F#" : "C#";
        tech.Add(new Technology(language, ".NET", framework, source, "512BD4"));

        if (rel("global.json") is string globalJson
            && JsonDocument.Parse(text(globalJson)).RootElement is { ValueKind: JsonValueKind.Object } global
            && global.TryGetProperty("sdk", out JsonElement sdk)
            && sdk.TryGetProperty("version", out JsonElement version))
        {
            tech.Add(new Technology("SDK", ".NET SDK", version.GetString() ?? "", globalJson, "68217A"));
        }

        if (web)
        {
            tech.Add(new Technology("ASP", "ASP.NET Core", framework, source, "5C2D91"));
        }

        if (blazor)
        {
            tech.Add(new Technology("BZ", "Blazor", framework, source, "5C2D91"));
        }

        if (winui)
        {
            tech.Add(new Technology("WU", "WinUI 3", framework, source, "0067C0"));
        }

        if (wpf)
        {
            tech.Add(new Technology("WPF", "WPF", framework, source, "0067C0"));
        }

        if (winforms)
        {
            tech.Add(new Technology("WF", "Windows Forms", framework, source, "0067C0"));
        }

        if (maui)
        {
            tech.Add(new Technology("MA", ".NET MAUI", framework, source, "512BD4"));
        }

        string target2 = solution is not null ? $" \"{solution}\"" : projects.Count == 1 ? $" \"{projects[0]}\"" : "";
        run.Add(new RunCommand(".NET", "Restore", $"dotnet restore{target2}", RunShell.PowerShell));
        run.Add(new RunCommand(".NET", "Build", $"dotnet build{target2}", RunShell.PowerShell));
        run.Add(new RunCommand(".NET", "Test", $"dotnet test{target2}", RunShell.PowerShell));
    }

    // ---- Node ------------------------------------------------------------------------------------

    private static readonly (string Package, string Label, string Name, string Color)[] _nodeFrameworks =
    [
        ("typescript", "TS", "TypeScript", "3178C6"),
        ("react", "Re", "React", "087EA4"),
        ("next", "Nx", "Next.js", "000000"),
        ("vue", "Vue", "Vue", "42B883"),
        ("nuxt", "Nu", "Nuxt", "00DC82"),
        ("@angular/core", "Ng", "Angular", "DD0031"),
        ("svelte", "Sv", "Svelte", "FF3E00"),
        ("electron", "El", "Electron", "47848F"),
        ("react-native", "RN", "React Native", "087EA4"),
        ("expo", "Ex", "Expo", "000020"),
        ("express", "Exp", "Express", "404040"),
        ("vite", "Vi", "Vite", "646CFF"),
        ("tailwindcss", "Tw", "Tailwind CSS", "0EA5E9")
    ];

    private static void Node(Func<string, string?> rel, Func<string, string> text, List<Technology> tech, List<RunCommand> run)
    {
        if (rel("package.json") is not string packageJson)
        {
            return;
        }

        JsonElement package = JsonDocument.Parse(text(packageJson)).RootElement;

        string node = rel(".nvmrc") is string nvmrc ? text(nvmrc).Trim()
            : rel(".node-version") is string nodeVersion ? text(nodeVersion).Trim()
            : Property(package, "engines", "node");

        tech.Add(new Technology("JS", "Node.js", node, packageJson, "3C873A"));

        foreach ((string name, string label, string display, string color) in _nodeFrameworks)
        {
            string version = Property(package, "dependencies", name);

            if (version.Length == 0)
            {
                version = Property(package, "devDependencies", name);
            }

            if (version.Length > 0)
            {
                tech.Add(new Technology(label, display, version.TrimStart('^', '~', '='), packageJson, color));
            }
        }

        string manager = rel("pnpm-lock.yaml") is not null ? "pnpm"
            : rel("yarn.lock") is not null ? "yarn"
            : rel("bun.lockb") is not null || rel("bun.lock") is not null ? "bun"
            : "npm";

        run.Add(new RunCommand($"{manager} scripts", "install", $"{manager} install", RunShell.PowerShell));

        if (package.TryGetProperty("scripts", out JsonElement scripts) && scripts.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty script in scripts.EnumerateObject())
            {
                run.Add(new RunCommand($"{manager} scripts", script.Name, $"{manager} run {script.Name}", RunShell.PowerShell));
            }
        }
    }

    // ---- Python ----------------------------------------------------------------------------------

    private static void Python(Func<string, string?> rel, Func<string, string> text, List<Technology> tech, List<RunCommand> run)
    {
        string? pyproject = rel("pyproject.toml");
        string? requirements = rel("requirements.txt");
        string? setup = rel("setup.py");

        if (pyproject is null && requirements is null && setup is null)
        {
            return;
        }

        string version = rel(".python-version") is string pinned ? text(pinned).Trim() : "";
        string source = pyproject ?? requirements ?? setup ?? "";
        string all = (pyproject is null ? "" : text(pyproject)) + "\n" + (requirements is null ? "" : text(requirements));

        if (version.Length == 0 && pyproject is not null && RequiresPython().Match(text(pyproject)) is { Success: true } requires)
        {
            version = requires.Groups[1].Value;
        }

        tech.Add(new Technology("Py", "Python", version, source, "3776AB"));

        foreach ((string package, string label, string name, string color) in new[]
        {
            ("django", "Dj", "Django", "0C4B33"),
            ("flask", "Fl", "Flask", "000000"),
            ("fastapi", "FA", "FastAPI", "009688"),
            ("pytest", "Pt", "pytest", "0A9EDC")
        })
        {
            if (Regex.IsMatch(all, $@"(?im)^\s*[""']?{Regex.Escape(package)}\b"))
            {
                tech.Add(new Technology(label, name, "", source, color));
            }
        }

        bool poetry = pyproject is not null && text(pyproject).Contains("[tool.poetry]", StringComparison.Ordinal);

        if (poetry)
        {
            run.Add(new RunCommand("Python", "poetry install", "poetry install", RunShell.PowerShell));
        }
        else if (requirements is not null)
        {
            run.Add(new RunCommand("Python", "Install requirements", $"python -m pip install -r \"{requirements}\"", RunShell.PowerShell));
        }

        if (all.Contains("pytest", StringComparison.OrdinalIgnoreCase))
        {
            run.Add(new RunCommand("Python", "Run tests", poetry ? "poetry run pytest" : "python -m pytest", RunShell.PowerShell));
        }
    }

    // ---- Everything else -------------------------------------------------------------------------

    private static void Others(
        Func<string, string?> rel,
        Func<string, IEnumerable<string>> named,
        Func<string, IEnumerable<string>> ending,
        Func<string, string> text,
        List<Technology> tech,
        List<RunCommand> run)
    {
        if (rel("Cargo.toml") is string cargo)
        {
            string content = text(cargo);
            string version = RustVersion().Match(content) is { Success: true } rust ? rust.Groups[1].Value
                : Edition().Match(content) is { Success: true } edition ? $"edition {edition.Groups[1].Value}" : "";
            tech.Add(new Technology("Rs", "Rust", version, cargo, "CE422B"));
            run.Add(new RunCommand("Cargo", "Build", "cargo build", RunShell.PowerShell));
            run.Add(new RunCommand("Cargo", "Test", "cargo test", RunShell.PowerShell));
            run.Add(new RunCommand("Cargo", "Run", "cargo run", RunShell.PowerShell));
        }

        if (rel("go.mod") is string goMod)
        {
            tech.Add(new Technology("Go", "Go", GoVersion().Match(text(goMod)) is { Success: true } go ? go.Groups[1].Value : "", goMod, "00ADD8"));
            run.Add(new RunCommand("Go", "Build", "go build ./...", RunShell.PowerShell));
            run.Add(new RunCommand("Go", "Test", "go test ./...", RunShell.PowerShell));
        }

        string? gradle = rel("build.gradle.kts") ?? rel("build.gradle") ?? rel("settings.gradle.kts") ?? rel("settings.gradle");

        if (gradle is not null)
        {
            string wrapperVersion = rel(@"gradle\wrapper\gradle-wrapper.properties") is string wrapper
                && GradleVersion().Match(text(wrapper)) is { Success: true } g ? g.Groups[1].Value : "";
            bool kotlin = gradle.EndsWith(".kts", StringComparison.OrdinalIgnoreCase) || text(gradle).Contains("kotlin", StringComparison.OrdinalIgnoreCase);
            tech.Add(new Technology(kotlin ? "Kt" : "Jv", kotlin ? "Kotlin" : "Java", "", gradle, kotlin ? "7F52FF" : "E76F00"));
            tech.Add(new Technology("Gr", "Gradle", wrapperVersion, gradle, "02303A"));

            if (rel("AndroidManifest.xml") is not null || text(gradle).Contains("com.android", StringComparison.Ordinal))
            {
                tech.Add(new Technology("An", "Android", "", gradle, "3DDC84"));
            }

            string gradlew = rel("gradlew.bat") is not null ? @".\gradlew.bat" : "gradle";
            run.Add(new RunCommand("Gradle", "Build", $"{gradlew} build", RunShell.PowerShell));
            run.Add(new RunCommand("Gradle", "Test", $"{gradlew} test", RunShell.PowerShell));
        }

        if (rel("pom.xml") is string pom)
        {
            string java = JavaVersion().Match(text(pom)) is { Success: true } j ? j.Groups[1].Value : "";
            tech.Add(new Technology("Jv", "Java", java, pom, "E76F00"));
            tech.Add(new Technology("Mv", "Maven", "", pom, "C71A36"));
            string mvn = rel("mvnw.cmd") is not null ? @".\mvnw.cmd" : "mvn";
            run.Add(new RunCommand("Maven", "Package", $"{mvn} package", RunShell.PowerShell));
            run.Add(new RunCommand("Maven", "Test", $"{mvn} test", RunShell.PowerShell));
        }

        if (rel("pubspec.yaml") is string pubspec)
        {
            string content = text(pubspec);
            bool flutter = content.Contains("flutter:", StringComparison.Ordinal);
            string sdk = DartSdk().Match(content) is { Success: true } d ? d.Groups[1].Value : "";
            tech.Add(new Technology(flutter ? "Fl" : "Dt", flutter ? "Flutter" : "Dart", sdk, pubspec, flutter ? "02569B" : "0175C2"));
            string tool = flutter ? "flutter" : "dart";
            run.Add(new RunCommand(flutter ? "Flutter" : "Dart", "Get packages", $"{tool} pub get", RunShell.PowerShell));

            if (flutter)
            {
                run.Add(new RunCommand("Flutter", "Run", "flutter run", RunShell.PowerShell));
                run.Add(new RunCommand("Flutter", "Test", "flutter test", RunShell.PowerShell));
            }
        }

        if (rel("Gemfile") is string gemfile)
        {
            string ruby = rel(".ruby-version") is string pinned ? text(pinned).Trim() : "";
            tech.Add(new Technology("Rb", "Ruby", ruby, gemfile, "CC342D"));

            if (text(gemfile).Contains("rails", StringComparison.Ordinal))
            {
                tech.Add(new Technology("Ra", "Rails", "", gemfile, "D30001"));
            }

            run.Add(new RunCommand("Ruby", "bundle install", "bundle install", RunShell.PowerShell));
        }

        if (rel("composer.json") is string composer)
        {
            tech.Add(new Technology("PHP", "PHP", Property(JsonDocument.Parse(text(composer)).RootElement, "require", "php"), composer, "777BB4"));
            run.Add(new RunCommand("PHP", "composer install", "composer install", RunShell.PowerShell));
        }

        if (rel("CMakeLists.txt") is string cmake)
        {
            string version = CmakeVersion().Match(text(cmake)) is { Success: true } c ? c.Groups[1].Value : "";
            tech.Add(new Technology("C++", "C/C++ (CMake)", version, cmake, "00599C"));
            run.Add(new RunCommand("CMake", "Configure and build", "cmake -S . -B build; cmake --build build", RunShell.PowerShell));
        }

        if (rel("Package.swift") is string swift)
        {
            tech.Add(new Technology("Sw", "Swift", SwiftTools().Match(text(swift)) is { Success: true } s ? s.Groups[1].Value : "", swift, "F05138"));
        }

        string? dockerfile = named("Dockerfile").FirstOrDefault();
        string? compose = rel("docker-compose.yml") ?? rel("docker-compose.yaml") ?? rel("compose.yml") ?? rel("compose.yaml");

        if (dockerfile is not null || compose is not null)
        {
            string image = dockerfile is not null && DockerFrom().Match(text(dockerfile)) is { Success: true } from ? $"from {from.Groups[1].Value}" : "";
            tech.Add(new Technology("Dk", "Docker", image, dockerfile ?? compose ?? "", "2496ED"));

            if (compose is not null)
            {
                run.Add(new RunCommand("Docker", "compose up", "docker compose up", RunShell.PowerShell));
                run.Add(new RunCommand("Docker", "compose build", "docker compose build", RunShell.PowerShell));
            }
        }

        if (rel("Makefile") is string makefile)
        {
            tech.Add(new Technology("Mk", "Make", "", makefile, "6D6D6D"));

            foreach (string target in MakeTarget().Matches(text(makefile)).Select(m => m.Groups[1].Value).Distinct().Take(20))
            {
                run.Add(new RunCommand("Make", target, $"make {target}", RunShell.PowerShell));
            }
        }

        int workflows = ending(".yml").Concat(ending(".yaml")).Count(file => file.StartsWith(@".github\workflows\", StringComparison.OrdinalIgnoreCase));

        if (workflows > 0)
        {
            tech.Add(new Technology("GA", "GitHub Actions", $"{workflows} workflow{(workflows == 1 ? "" : "s")}", @".github\workflows", "2088FF"));
        }
    }

    // ---- Script files ----------------------------------------------------------------------------

    private static void Scripts(List<string> files, List<RunCommand> run)
    {
        IEnumerable<string> candidates = files.Where(file =>
        {
            string folder = file.Contains('\\') ? file[..file.IndexOf('\\')] : "";
            return (folder.Length == 0 || _scriptFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                && (file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".sh", StringComparison.OrdinalIgnoreCase));
        });

        foreach (string script in candidates.Take(MaxScripts))
        {
            string extension = Path.GetExtension(script).ToLowerInvariant();

            run.Add(extension switch
            {
                ".ps1" => new RunCommand("Scripts", script, $"& '.\\{script.Replace("'", "''", StringComparison.Ordinal)}'", RunShell.PowerShell),
                ".sh" => new RunCommand("Scripts", script, $"./{script.Replace('\\', '/')}", RunShell.Bash),
                _ => new RunCommand("Scripts", script, $"\"{script}\"", RunShell.Cmd)
            });
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>
    ///  Every file within reach, relative to the root, with backslashes. Breadth-first so shallow files
    ///  come first, which is how "the" solution or package file is chosen when there are several.
    /// </summary>
    private static List<string> ListFiles(string root)
    {
        List<string> files = [];
        Queue<(string Path, int Depth)> folders = new();
        folders.Enqueue((root, 0));
        int visited = 0;

        while (folders.Count > 0 && visited++ < MaxFolders)
        {
            (string folder, int depth) = folders.Dequeue();

            try
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    files.Add(Path.GetRelativePath(root, file));
                }

                if (depth >= MaxDepth)
                {
                    continue;
                }

                foreach (string child in Directory.EnumerateDirectories(folder))
                {
                    string name = Path.GetFileName(child);

                    // .github is the one hidden folder worth reading: it holds the workflows.
                    if (!_skipped.Contains(name) && (!name.StartsWith('.') || name.Equals(".github", StringComparison.OrdinalIgnoreCase)))
                    {
                        folders.Enqueue((child, depth + 1));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be read is skipped, not fatal.
            }
        }

        return files;
    }

    /// <summary>A string property one level down, such as engines.node; empty when it is not there.</summary>
    private static string Property(JsonElement element, string section, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(section, out JsonElement inner)
        && inner.ValueKind == JsonValueKind.Object
        && inner.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    [GeneratedRegex(@"<TargetFrameworks?>([^<]+)<", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFramework();

    [GeneratedRegex(@"requires-python\s*=\s*[""']([^""']+)[""']")]
    private static partial Regex RequiresPython();

    [GeneratedRegex(@"rust-version\s*=\s*""([^""]+)""")]
    private static partial Regex RustVersion();

    [GeneratedRegex(@"edition\s*=\s*""([^""]+)""")]
    private static partial Regex Edition();

    [GeneratedRegex(@"(?m)^go\s+(\S+)")]
    private static partial Regex GoVersion();

    [GeneratedRegex(@"gradle-([\d.]+)-")]
    private static partial Regex GradleVersion();

    [GeneratedRegex(@"<(?:java\.version|maven\.compiler\.(?:source|release))>([^<]+)<")]
    private static partial Regex JavaVersion();

    [GeneratedRegex(@"sdk:\s*[""']?([^""'\r\n]+)")]
    private static partial Regex DartSdk();

    [GeneratedRegex(@"cmake_minimum_required\s*\(\s*VERSION\s+([\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CmakeVersion();

    [GeneratedRegex(@"swift-tools-version:\s*([\d.]+)")]
    private static partial Regex SwiftTools();

    [GeneratedRegex(@"(?im)^\s*FROM\s+(\S+)")]
    private static partial Regex DockerFrom();

    [GeneratedRegex(@"(?m)^([A-Za-z0-9][A-Za-z0-9_.-]*)\s*:(?!=)")]
    private static partial Regex MakeTarget();
}
