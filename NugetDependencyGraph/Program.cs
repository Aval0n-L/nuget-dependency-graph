using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace NugetDependencyGraph
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            // Interactive mode
            if (args.Length == 0)
            {
                return RunInteractiveMode();
            }

            // Command line mode (original functionality)
            string inputPath = args[0];
            inputPath = Path.GetFullPath(inputPath.Trim().Trim('"'));
            string? tfm = null;
            bool mermaid = false;
            string? outPath = null;
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a.Equals("--mermaid", StringComparison.OrdinalIgnoreCase))
                {
                    mermaid = true;
                }
                else if (a.StartsWith("--tfm", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = a.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1])) tfm = parts[1];
                    else if (i + 1 < args.Length) tfm = args[++i];
                }
                else if (a == "--out" || a == "-o")
                {
                    if (i + 1 < args.Length) outPath = args[++i];
                }
                else if (a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-o=", StringComparison.OrdinalIgnoreCase))
                {
                    outPath = a.Split('=', 2)[1];
                }
            }

            return AnalyzeProject(inputPath, tfm, mermaid, outPath);
        }

        static int RunInteractiveMode()
        {
            Console.WriteLine("🔍 NuGet Dependency Graph Analyzer");
            Console.WriteLine("==================================");
            Console.WriteLine($"📂 Output directory: {Environment.CurrentDirectory}");
            Console.WriteLine($"🕐 Session started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine();

            while (true)
            {
                Console.WriteLine("Enter project path (.csproj, project folder, or project.assets.json):");
                Console.WriteLine("Or type 'exit' to quit, 'help' for options");
                Console.Write("> ");
                
                var input = Console.ReadLine()?.Trim();
                
                if (string.IsNullOrEmpty(input))
                    continue;
                
                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Goodbye! 👋");
                    return 0;
                }
                
                if (input.Equals("help", StringComparison.OrdinalIgnoreCase))
                {
                    ShowHelp();
                    continue;
                }

                // Parse any options from input
                var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var projectPath = parts[0].Trim('"');
                
                string? tfm = null;
                bool mermaid = false;
                
                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i].Equals("--mermaid", StringComparison.OrdinalIgnoreCase))
                        mermaid = true;
                    else if (parts[i].StartsWith("--tfm=", StringComparison.OrdinalIgnoreCase))
                        tfm = parts[i][6..];
                }

                Console.WriteLine();
                Console.WriteLine($"🔄 Analyzing: {projectPath}");
                Console.WriteLine(new string('-', 50));
                
                var result = AnalyzeProject(projectPath, tfm, mermaid, null);
                
                Console.WriteLine(new string('-', 50));
                if (result == 0)
                {
                    Console.WriteLine("✅ Analysis completed successfully!");
                    Console.WriteLine("📁 Check the generated files above for graph visualization");
                    Console.WriteLine("💡 PNG files work best for viewing and sharing");
                }
                else
                    Console.WriteLine($"❌ Analysis failed with code: {result}");
                    
                Console.WriteLine();
            }
        }

        static void ShowHelp()
        {
            Console.WriteLine();
            Console.WriteLine("📚 Help - Usage Options:");
            Console.WriteLine("  <path>                    - Path to .csproj, folder, or project.assets.json");
            Console.WriteLine("  <path> --mermaid          - Output in Mermaid format (.mmd)");
            Console.WriteLine("  <path> --tfm=net8.0       - Specify target framework");
            Console.WriteLine();
            Console.WriteLine("🎯 Output formats (automatically generated):");
            Console.WriteLine("  📄 Source: .dot/.mmd files");
            Console.WriteLine("  🖼️ PNG: Image files (requires Graphviz)");
            Console.WriteLine("  📐 SVG: Scalable vector graphics (requires Graphviz)");
            Console.WriteLine();
            Console.WriteLine("💡 The tool will automatically open the best available format!");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  C:\\MyProject\\MyApp.csproj");
            Console.WriteLine("  C:\\MyProject");
            Console.WriteLine("  ../AnotherProject --mermaid");
            Console.WriteLine("  . --tfm=net8.0");
            Console.WriteLine();
        }
        
        static int AnalyzeProject(string inputPath, string? tfm, bool mermaid, string? outPath)

        {
            try
            {
                inputPath = Path.GetFullPath(inputPath.Trim().Trim('"'));
                
                var assetsPath = ResolveAssetsJson(inputPath);
                if (assetsPath == null)
                {
                    var projectDir = ResolveProjectDirectory(inputPath);
                    if (projectDir == null)
                    {
                        Console.Error.WriteLine("❌ Unable to resolve project directory.");
                        Console.Error.WriteLine($"   Tried: {inputPath}");
                        return 3;
                    }
                    
                    Console.WriteLine($"🔄 Running 'dotnet restore' in {projectDir}...");
                    if (!RunDotnetRestore(projectDir))
                    {
                        Console.Error.WriteLine("❌ dotnet restore failed.");
                        return 4;
                    }
                    
                    assetsPath = ResolveAssetsJson(projectDir) ?? ResolveAssetsJson(inputPath);
                    if (assetsPath == null)
                    {
                        Console.Error.WriteLine("❌ project.assets.json not found after restore.");
                        return 5;
                    }
                }

                Console.WriteLine($"📄 Found assets: {Path.GetRelativePath(Environment.CurrentDirectory, assetsPath)}");

                using var fs = File.OpenRead(assetsPath);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;

                if (!root.TryGetProperty("targets", out var targets))
                {
                    Console.Error.WriteLine("❌ Invalid project.assets.json: 'targets' not found.");
                    return 6;
                }

                string chosenTfm = ChooseTfm(targets, tfm);
                Console.WriteLine($"🎯 Target Framework: {chosenTfm}");
                
                if (!targets.TryGetProperty(chosenTfm, out var targetObj))
                {
                    Console.Error.WriteLine($"❌ TFM '{chosenTfm}' not present in 'targets'.");
                    return 7;
                }

                var nameToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var libProp in targetObj.EnumerateObject())
                {
                    var key = libProp.Name; // e.g. "Newtonsoft.Json/13.0.3"
                    var slash = key.IndexOf('/');
                    var name = slash > 0 ? key[..slash] : key;
                    if (!nameToKey.ContainsKey(name)) nameToKey[name] = key;
                }

                var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var edges = new HashSet<(string From, string To)>();

                // Build package dependency graph
                Console.WriteLine("🔗 Building package dependency graph...");
                foreach (var libProp in targetObj.EnumerateObject())
                {
                    var key = libProp.Name;
                    nodes.Add(key);

                    var lib = libProp.Value;
                    if (lib.ValueKind != JsonValueKind.Object) continue;

                    if (!lib.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
                        continue;

                    foreach (var dep in deps.EnumerateObject())
                    {
                        var depName = dep.Name;
                        if (!nameToKey.TryGetValue(depName, out var depKey))
                        {
                            depKey = targetObj.EnumerateObject()
                                .Select(p => p.Name)
                                .FirstOrDefault(k => k.StartsWith(depName + "/", StringComparison.OrdinalIgnoreCase));
                            if (depKey == null) continue;
                            nameToKey[depName] = depKey;
                        }
                        nodes.Add(depKey);
                        edges.Add((key, depKey));
                    }
                }

                // Add root project
                var rootName = Path.GetFileNameWithoutExtension(inputPath);
                var rootKey = rootName + "/(project)";
                nodes.Add(rootKey);

                // Process project references
                Console.WriteLine("📦 Processing project references...");
                if (root.TryGetProperty("project", out var projX) &&
                    projX.TryGetProperty("projectReferences", out var prjRefsX) &&
                    prjRefsX.ValueKind == JsonValueKind.Object)
                {
                    foreach (var pr in prjRefsX.EnumerateObject())
                    {
                        var prjName = Path.GetFileNameWithoutExtension(pr.Name);
                        var childKey = prjName + "/(project)";
                        nodes.Add(childKey);
                        edges.Add((rootKey, childKey));
                        Console.WriteLine($"   📁 Found project: {prjName}");
                    }
                }
                
                AddProjectReferencesFromCsproj(inputPath, rootKey, nodes, edges);
                var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AddProjectReferencesRecursive(inputPath, rootKey, tfm, nodes, edges, visitedProjects);
                CollapseProjectPackageDuplicates(nodes, edges);
                
                Console.WriteLine($"📊 Found {nodes.Count} nodes and {edges.Count} dependencies");
                
                string text = mermaid
                    ? BuildMermaid(nodes, edges, assetsPath, chosenTfm)
                    : BuildDot(nodes, edges, assetsPath, chosenTfm);

                // Always save to file, even if not explicitly specified
                var outputFiles = GenerateAndSaveAllFormats(inputPath, text, mermaid, outPath);
                
                if (!string.IsNullOrEmpty(outPath))
                {
                    // User specified custom output path
                    var utf8NoBom = new UTF8Encoding(false);
                    File.WriteAllText(outPath, text, utf8NoBom);
                    Console.WriteLine($"💾 Graph saved to: {Path.GetFullPath(outPath)}");
                    TryOpenFile(outPath);
                }
                else
                {
                    // Auto-save with generated filename and multiple formats
                    Console.WriteLine($"📁 Location: {Path.GetDirectoryName(Path.GetFullPath(outputFiles.baseFile))}");
                    
                    // Also display in console for immediate viewing
                    Console.WriteLine();
                    Console.WriteLine("📈 DEPENDENCY GRAPH:");
                    Console.WriteLine(new string('=', 50));
                    Console.OutputEncoding = new UTF8Encoding(false);
                    Console.Write(text);
                    Console.WriteLine(new string('=', 50));
                    
                    // Open the best available format
                    OpenBestFormat(outputFiles);
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"❌ Error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.Error.WriteLine($"   Inner: {ex.InnerException.Message}");
                }
                return 1;
            }
        }

        private static string? ResolveAssetsJson(string path)
        {
            if (File.Exists(path) &&
                Path.GetFileName(path).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase))
                return path;

            if (File.Exists(path) &&
                Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
                var candidate = Path.Combine(dir, "obj", "project.assets.json");
                if (!File.Exists(candidate))
                {
                    var objDir = Path.Combine(dir, "obj");
                    if (Directory.Exists(objDir))
                    {
                        var alt = Directory.EnumerateFiles(objDir, "project.assets.json", SearchOption.AllDirectories).FirstOrDefault();
                        if (alt != null) return alt;
                    }
                }
                return File.Exists(candidate) ? candidate : null;
            }

            if (Directory.Exists(path))
            {
                var dir = Path.GetFullPath(path);
                var candidate = Path.Combine(dir, "obj", "project.assets.json");
                if (File.Exists(candidate)) return candidate;

                var csproj = Directory.EnumerateFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (csproj != null)
                {
                    var c2 = Path.Combine(dir, "obj", "project.assets.json");
                    if (File.Exists(c2)) return c2;
                }
            }

            return null;
        }

        private static string? ResolveProjectDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            var p = path.Trim().Trim('"');
            try { p = Path.GetFullPath(p); } catch { /* ignore */ }

            // 1) Exact .csproj
            if (File.Exists(p) && string.Equals(Path.GetExtension(p), ".csproj", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(p);

            // 2) Folder where .csproj is located
            if (Directory.Exists(p))
            {
                var csproj = Directory.EnumerateFiles(p, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (csproj != null) return Path.GetDirectoryName(csproj);
                return p; // folder exists, but no .csproj - still return as project folder
            }

            // 3) Attempt: if string looks like path to .csproj, but File.Exists returned false (for example, due to // or spaces)
            if (p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    return dir;
            }

            return null;
        }

        private static bool RunDotnetRestore(string projectDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "restore",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }

        private static string ChooseTfm(JsonElement targets, string? requestedTfm)
        {
            if (!string.IsNullOrWhiteSpace(requestedTfm))
            {
                if (targets.TryGetProperty(requestedTfm, out _))
                    return requestedTfm;

                var pref = targets.EnumerateObject()
                    .Select(p => p.Name)
                    .FirstOrDefault(n => n.StartsWith(requestedTfm, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(pref))
                    return pref;
            }

            var first = targets.EnumerateObject().FirstOrDefault();
            if (string.IsNullOrEmpty(first.Name))
                throw new InvalidOperationException("No target frameworks in assets.");
            return first.Name;
        }

        private static string BuildDot(HashSet<string> nodes, HashSet<(string From, string To)> edges, string assetsPath, string tfm)
        {
            static string DotId(string key) => "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

            var sb = new StringBuilder();
            sb.AppendLine("digraph NuGetDeps {");
            sb.AppendLine("  rankdir=LR;");
            sb.AppendLine("  node [shape=box, fontsize=10];");
            sb.AppendLine($"  label=\"NuGet dependencies for {Path.GetFileName(assetsPath)}\\nTFM: {tfm}\"; labelloc=top; fontsize=12;");

            foreach (var key in nodes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var slash = key.IndexOf('/');
                var label = slash > 0 ? key[..slash] + "\\n" + key[(slash + 1)..] : key;
                sb.AppendLine($"  {DotId(key)} [label=\"{label}\"];");
            }

            foreach (var (from, to) in edges.OrderBy(e => e.From).ThenBy(e => e.To))
                sb.AppendLine($"  {DotId(from)} -> {DotId(to)};");

            sb.AppendLine("}"); 
            return sb.ToString();
        }

        private static string BuildMermaid(HashSet<string> nodes, HashSet<(string From, string To)> edges, string assetsPath, string tfm)
        {
            static string Id(string key)
            {
                // Allow only letters/digits/underscore
                var sb = new StringBuilder(key.Length);
                foreach (var ch in key)
                    sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
                return sb.ToString();
            }
            var sb = new StringBuilder();
            sb.AppendLine("%% Mermaid graph (paste into Markdown)");
            sb.AppendLine("graph LR");
            sb.AppendLine($"  %% {Path.GetFileName(assetsPath)} | {tfm}");

            foreach (var key in nodes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var id = Id(key);
                var slash = key.IndexOf('/');
                var name = slash > 0 ? key[..slash] : key;
                var ver = slash > 0 ? key[(slash + 1)..] : "";
                var label = string.IsNullOrEmpty(ver) ? name : $"{name} ({ver})";
                sb.AppendLine($"  {id}[\"{label}\"]");
            }

            foreach (var (from, to) in edges.OrderBy(e => e.From).ThenBy(e => e.To))
                sb.AppendLine($"  {Id(from)} --> {Id(to)}");
            return sb.ToString();
        }

        private static void AddProjectReferencesRecursive(
            string projectOrAssetsPath,
            string parentProjectKey,                 // for example "MyProj/(project)"
            string? requestedTfm,
            HashSet<string> nodes,
            HashSet<(string From, string To)> edges,
            HashSet<string> visited)
        {
            // Normalize input
            var p = projectOrAssetsPath.Trim().Trim('"');
            try { p = Path.GetFullPath(p); } catch { /* ignore */ }

            // 1) Find assets.json for this project
            string? assets;
            string projectPath;

            if (File.Exists(p) && Path.GetExtension(p).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                projectPath = p;
                assets = ResolveAssetsJson(projectPath);
            }
            else if (File.Exists(p) && Path.GetFileName(p).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase))
            {
                assets = p;
                // go up to 'obj', then one more level - to project folder
                var dir = Path.GetDirectoryName(assets)!;                 // ...\obj\NETTFM
                while (!string.Equals(Path.GetFileName(dir), "obj", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Directory.GetParent(dir);
                    if (parent == null) break;
                    dir = parent.FullName;
                }
                var projDir = Directory.GetParent(dir)?.FullName ?? dir;  // ...\ (project folder)
                var cs = Directory.EnumerateFiles(projDir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                projectPath = cs ?? projDir;
            }
            else if (Directory.Exists(p))
            {
                // project folder
                projectPath = Directory.EnumerateFiles(p, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? p;
                assets = ResolveAssetsJson(projectPath);
            }
            else
            {
                return; // do nothing
            }

            if (assets == null || !File.Exists(assets))
                return;

            // Protection from cycles
            var visitKey = assets.ToLowerInvariant();
            if (!visited.Add(visitKey))
                return;

            using var fs = File.OpenRead(assets);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;

            if (!root.TryGetProperty("targets", out var targets))
                return;

            var chosenTfm = ChooseTfm(targets, requestedTfm);
            if (!targets.TryGetProperty(chosenTfm, out var targetObj))
                return;

            // Local map of packages for this project: Name -> "Name/Version"
            var localNameToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var libProp in targetObj.EnumerateObject())
            {
                var key = libProp.Name; // "Pkg/1.2.3"
                var slash = key.IndexOf('/');
                var name = slash > 0 ? key[..slash] : key;
                if (!localNameToKey.ContainsKey(name)) localNameToKey[name] = key;
            }

            // Project node (if we want to draw child projects as "(project)")
            var projName = File.Exists(projectPath) ? Path.GetFileNameWithoutExtension(projectPath) : Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar));
            var thisProjectKey = projName + "/(project)";

            // Add node and edge from parent to this project (if it's not the root)
            if (!string.Equals(parentProjectKey, thisProjectKey, StringComparison.Ordinal))
            {
                nodes.Add(thisProjectKey);
                edges.Add((parentProjectKey, thisProjectKey));
            }


            // 2) Package dependencies of THIS project (direct): project.frameworks[*].dependencies
            if (root.TryGetProperty("project", out var proj) &&
                proj.TryGetProperty("frameworks", out var fws))
            {
                foreach (var fw in fws.EnumerateObject())
                {
                    if (fw.Value.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var dep in deps.EnumerateObject())
                        {
                            if (localNameToKey.TryGetValue(dep.Name, out var depKey))
                            {
                                nodes.Add(depKey);
                                edges.Add((thisProjectKey, depKey));
                            }
                        }
                    }
                }
            }


            // 3) Internal package graph of this project (as done for root)
            foreach (var libProp in targetObj.EnumerateObject())
            {
                var key = libProp.Name;
                nodes.Add(key);

                var lib = libProp.Value;
                if (lib.ValueKind != JsonValueKind.Object) continue;

                if (lib.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
                {
                    foreach (var dep in deps.EnumerateObject())
                    {
                        if (!localNameToKey.TryGetValue(dep.Name, out var depKey))
                        {
                            depKey = targetObj.EnumerateObject().Select(p => p.Name)
                                      .FirstOrDefault(k => k.StartsWith(dep.Name + "/", StringComparison.OrdinalIgnoreCase));
                        }
                        if (depKey != null)
                        {
                            nodes.Add(depKey);
                            edges.Add((key, depKey));
                        }
                    }
                }
            }

            // 4) Recursively walk through ProjectReference of this project
            if (root.TryGetProperty("project", out var proj2) &&
                proj2.TryGetProperty("frameworks", out var fws2))
            {
                foreach (var fw in fws2.EnumerateObject())
                {
                    if (fw.Value.TryGetProperty("projectReferences", out var prjRefs) &&
                        prjRefs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var pr in prjRefs.EnumerateObject())
                        {
                            // pr.Name - relative path to .csproj
                            var childProjPath = File.Exists(projectPath)
                                ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, pr.Name))
                                : Path.GetFullPath(Path.Combine(projectPath, pr.Name));

                            AddProjectReferencesRecursive(childProjPath, thisProjectKey, requestedTfm, nodes, edges, visited);
                        }
                    }
                }
            }

            // 4a) ProjectReference at project.projectReferences level
            if (root.TryGetProperty("project", out var projPR) &&
                projPR.TryGetProperty("projectReferences", out var prTop) &&
                prTop.ValueKind == JsonValueKind.Object)
            {
                foreach (var pr in prTop.EnumerateObject())
                {
                    var childProjPath = File.Exists(projectPath)
                        ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, pr.Name))
                        : Path.GetFullPath(Path.Combine(projectPath, pr.Name));

                    var childName = Path.GetFileNameWithoutExtension(childProjPath);
                    var childKey = childName + "/(project)";
                    nodes.Add(childKey);
                    edges.Add((thisProjectKey, childKey));
                    // Console.WriteLine($"   📁 Project ref: {childName}");

                    AddProjectReferencesRecursive(childProjPath, thisProjectKey, requestedTfm, nodes, edges, visited);
                }
            }

        }
        private static void AddProjectReferencesFromCsproj(
            string projectOrAssetsPath,
            string parentProjectKey,
            HashSet<string> nodes,
            HashSet<(string From, string To)> edges)
        {
            // Calculate .csproj of this node
            string? csprojPath = null;
            var p = projectOrAssetsPath.Trim().Trim('"');
            try { p = Path.GetFullPath(p); } catch { /* ignore */ }

            if (File.Exists(p) && Path.GetExtension(p).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                csprojPath = p;
            }
            else if (File.Exists(p) && Path.GetFileName(p).Equals("project.assets.json", StringComparison.OrdinalIgnoreCase))
            {
                // go up to project folder and find .csproj
                var dir = Path.GetDirectoryName(p)!;
                while (!string.Equals(Path.GetFileName(dir), "obj", StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Directory.GetParent(dir);
                    if (parent == null) break;
                    dir = parent.FullName;
                }
                var projDir = Directory.GetParent(dir)?.FullName ?? dir;
                csprojPath = Directory.EnumerateFiles(projDir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }
            else if (Directory.Exists(p))
            {
                csprojPath = Directory.EnumerateFiles(p, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }

            if (csprojPath is null || !File.Exists(csprojPath))
                return;

            // Parse .csproj and extract <ProjectReference Include="...">
            XDocument doc;
            try { doc = XDocument.Load(csprojPath, LoadOptions.PreserveWhitespace); }
            catch { return; }

            var projDirName = Path.GetDirectoryName(csprojPath)!;
            var prNodes = doc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFullPath(
                    Path.IsPathRooted(include!) ? include! : Path.Combine(projDirName, include!)
                ))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var childProjPath in prNodes)
            {
                var childName = Path.GetFileNameWithoutExtension(childProjPath);
                var childKey = childName + "/(project)";
                nodes.Add(childKey);
                edges.Add((parentProjectKey, childKey));
                // Don't go deep here - that's what AddProjectReferencesRecursive(...) does
            }
        }
        private static void CollapseProjectPackageDuplicates(
            HashSet<string> nodes,
            HashSet<(string From, string To)> edges)
        {
            // Collect set of project names and project node keys
            var projectNameToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in nodes)
            {
                if (k.EndsWith("/(project)", StringComparison.Ordinal))
                {
                    var name = k[..k.IndexOf("/(project)", StringComparison.Ordinal)];
                    if (!projectNameToKey.ContainsKey(name)) projectNameToKey[name] = k;
                }
            }

            // Find package nodes whose name matches project name
            var packageToProject = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in nodes)
            {
                if (k.EndsWith("/(project)", StringComparison.Ordinal)) continue; // this is a project
                var slash = k.IndexOf('/');
                if (slash <= 0) continue; // safe
                var name = k[..slash];     // "ConsoleApp1" in "ConsoleApp1/1.0.0"
                if (projectNameToKey.TryGetValue(name, out var projKey))
                {
                    packageToProject[k] = projKey;
                }
            }

            if (packageToProject.Count == 0) return;

            // Rewrite edges: all references to package → to project with the same name
            var newEdges = new HashSet<(string From, string To)>();
            foreach (var (from, to) in edges)
            {
                var newFrom = packageToProject.TryGetValue(from, out var pf) ? pf : from;
                var newTo = packageToProject.TryGetValue(to, out var pt) ? pt : to;

                // remove possible self-loops after collapsing
                if (!string.Equals(newFrom, newTo, StringComparison.OrdinalIgnoreCase))
                    newEdges.Add((newFrom, newTo));
            }
            edges.Clear();
            foreach (var e in newEdges) edges.Add(e);

            // Remove package duplicates from node set
            foreach (var pkgKey in packageToProject.Keys)
                nodes.Remove(pkgKey);
        }

        private class OutputFiles
        {
            public string baseFile { get; set; } = "";
            public string? pngFile { get; set; }
            public string? svgFile { get; set; }
        }

        private static OutputFiles GenerateAndSaveAllFormats(string inputPath, string graphContent, bool mermaid, string? customOutPath)
        {
            var files = new OutputFiles();
            var utf8NoBom = new UTF8Encoding(false);
            
            // 1. Save base format (DOT or Mermaid)
            files.baseFile = GenerateOutputFilename(inputPath, mermaid);
            File.WriteAllText(files.baseFile, graphContent, utf8NoBom);
            Console.WriteLine($"💾 {(mermaid ? "Mermaid" : "DOT")} saved to: {Path.GetFullPath(files.baseFile)}");
            
            // 2. If DOT format, try to generate PNG and SVG
            if (!mermaid)
            {
                if (IsGraphvizAvailable())
                {
                    files.pngFile = ConvertDotToPng(files.baseFile);
                    files.svgFile = ConvertDotToSvg(files.baseFile);
                    
                    if (files.pngFile != null)
                        Console.WriteLine($"🖼️ PNG saved to: {Path.GetFullPath(files.pngFile)}");
                    if (files.svgFile != null)
                        Console.WriteLine($"📐 SVG saved to: {Path.GetFullPath(files.svgFile)}");
                }
                else
                {
                    Console.WriteLine("⚠️  Graphviz not found - install it for PNG/SVG generation");
                    Console.WriteLine("   Download: https://graphviz.org/download/");
                }
            }
            
            return files;
        }

        private static bool IsGraphvizAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "dot",
                    Arguments = "-V",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                process?.WaitForExit();
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static string? ConvertDotToPng(string dotFile)
        {
            try
            {
                var pngFile = Path.ChangeExtension(dotFile, "png");
                var psi = new ProcessStartInfo
                {
                    FileName = "dot",
                    Arguments = $"-Tpng \"{dotFile}\" -o \"{pngFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                process?.WaitForExit();
                
                return process?.ExitCode == 0 && File.Exists(pngFile) ? pngFile : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? ConvertDotToSvg(string dotFile)
        {
            try
            {
                var svgFile = Path.ChangeExtension(dotFile, "svg");
                var psi = new ProcessStartInfo
                {
                    FileName = "dot",
                    Arguments = $"-Tsvg \"{dotFile}\" -o \"{svgFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                using var process = Process.Start(psi);
                process?.WaitForExit();
                
                return process?.ExitCode == 0 && File.Exists(svgFile) ? svgFile : null;
            }
            catch
            {
                return null;
            }
        }

        private static void OpenBestFormat(OutputFiles files)
        {
            // Priority: PNG (visual) > SVG > original
            if (files.pngFile != null && File.Exists(files.pngFile))
            {
                Console.WriteLine("🖼️ Opening PNG image...");
                TryOpenFile(files.pngFile);
            }
            else if (files.svgFile != null && File.Exists(files.svgFile))
            {
                Console.WriteLine("📐 Opening SVG image...");
                TryOpenFile(files.svgFile);
            }
            else
            {
                Console.WriteLine($"📄 Opening {Path.GetExtension(files.baseFile)} file...");
                TryOpenFile(files.baseFile);
            }
        }

        private static string GenerateOutputFilename(string inputPath, bool mermaid)
        {
            // Get project name from path
            string projectName;
            if (File.Exists(inputPath) && Path.GetExtension(inputPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                projectName = Path.GetFileNameWithoutExtension(inputPath);
            }
            else if (Directory.Exists(inputPath))
            {
                var csproj = Directory.EnumerateFiles(inputPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                projectName = csproj != null ? Path.GetFileNameWithoutExtension(csproj) : Path.GetFileName(inputPath.TrimEnd(Path.DirectorySeparatorChar));
            }
            else
            {
                projectName = Path.GetFileNameWithoutExtension(inputPath);
            }

            // Clean project name for filename
            projectName = string.IsNullOrEmpty(projectName) ? "dependencies" : projectName;
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                projectName = projectName.Replace(c, '_');
            }

            // Generate timestamped filename
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var extension = mermaid ? "mmd" : "dot";
            var fileName = $"{projectName}_dependencies_{timestamp}.{extension}";

            // Save in current directory or user temp folder
            var outputDir = Environment.CurrentDirectory;
            return Path.Combine(outputDir, fileName);
        }

        private static void TryOpenFile(string filePath)
        {
            try
            {
                Console.WriteLine("🚀 Attempting to open graph file...");
                
                // Try to open the file with default application
                var psi = new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true,
                    Verb = "open"
                };
                
                var process = Process.Start(psi);
                if (process != null)
                {
                    Console.WriteLine("✅ Graph file opened successfully!");
                    var extension = Path.GetExtension(filePath).ToLower();
                    switch (extension)
                    {
                        case ".png":
                            Console.WriteLine("🖼️ PNG image opened - save or share this visual graph!");
                            break;
                        case ".svg":
                            Console.WriteLine("📐 SVG opened - scalable vector graphic!");
                            break;
                        case ".dot":
                            Console.WriteLine("💡 DOT file: Use VS Code Graphviz extension or https://dreampuf.github.io/GraphvizOnline/");
                            break;
                        case ".mmd":
                            Console.WriteLine("💡 Mermaid file: Use https://mermaid.live/ or GitHub markdown");
                            break;
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ File saved but could not auto-open. Please open manually.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Could not auto-open file: {ex.Message}");
                Console.WriteLine($"📂 Please manually open: {filePath}");
                
                var extension = Path.GetExtension(filePath).ToLower();
                switch (extension)
                {
                    case ".png":
                        Console.WriteLine("💡 Double-click the PNG file to view the graph image");
                        break;
                    case ".svg":
                        Console.WriteLine("💡 Open SVG file in browser or image viewer");
                        break;
                    case ".dot":
                        Console.WriteLine("💡 For DOT files: https://dreampuf.github.io/GraphvizOnline/");
                        break;
                    case ".mmd":
                        Console.WriteLine("💡 For Mermaid files: https://mermaid.live/");
                        break;
                }
            }
        }

    }
}
