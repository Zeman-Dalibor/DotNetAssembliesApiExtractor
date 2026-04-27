using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace DotNetAssembliesApiExtractor.Services
{
    /// <summary>
    /// Discovers and prioritizes assembly paths needed to create a MetadataLoadContext resolver.
    /// Handles TPA, .NET Framework system dirs, .NET Core runtimes, WPF Desktop, TFM refs,
    /// user-provided refs, local assemblies, and .config probing paths.
    /// </summary>
    internal class AssemblyReferenceResolver
    {
        private readonly string? _referenceAssembliesDir;
        private readonly bool _verbose;

        public AssemblyReferenceResolver(string? referenceAssembliesDir = null, bool verbose = false)
        {
            _referenceAssembliesDir = referenceAssembliesDir;
            _verbose = verbose;
        }

        /// <summary>
        /// Collects all assembly paths needed for MetadataLoadContext resolution,
        /// deduplicated by file name with priority-based override (last write wins).
        /// </summary>
        public List<string> CollectResolverPaths(string assemblyPath)
        {
            if (_verbose) Console.WriteLine($"Collecting resolver paths for: {assemblyPath}");

            // Detect target framework early so we can prioritize correct assemblies during dedup
            string? detectedTfm = null;
            try { detectedTfm = GetTargetFrameworkFromAssembly(assemblyPath); } catch { }
            var isNetFramework = !string.IsNullOrEmpty(detectedTfm) &&
                detectedTfm!.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase);
            // Many .NET Framework assemblies (especially 3rd-party) lack [TargetFramework].
            // Fall back to checking whether the assembly references mscorlib — a reliable .NET Fx indicator.
            if (!isNetFramework && string.IsNullOrEmpty(detectedTfm))
            {
                try { isNetFramework = ReferencesAssembly(assemblyPath, "mscorlib"); } catch { }
            }
            if (_verbose) Console.WriteLine($"  [TFM] Detected: {detectedTfm ?? "(none)"}, isNetFramework={isNetFramework}");

            // Collect paths into priority groups.
            // The dedup map is built from lowest to highest priority (last write wins).
            // This mirrors runtime resolution: local assemblies > user refs > TFM refs > system > fallbacks.
            //
            // NOTE: TPA and RuntimeEnvironment.GetRuntimeDirectory() return the SCANNER'S runtime
            // (e.g. .NET 6), not the target assembly's runtime. They belong to the fallback group
            // and must not override TFM-detected or .NET Framework assemblies.
            var localPaths = new List<string>();       // P5 (highest) — assemblies next to target
            var userRefPaths = new List<string>();     // P4 — user-provided --refsDir
            var tfmRefPaths = new List<string>();      // P3 — TFM reference assemblies (correct target runtime)
            var netFxSysPaths = new List<string>();    // P2 — .NET Framework directory + WPF (system-installed)
            var fallbackPaths = new List<string>();    // P1 (lowest) — scanner's TPA/runtime, AppDomain, installed runtimes

            // --- Collect: TPA (scanner's own runtime — fallback only) ---
            try
            {
                var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
                if (!string.IsNullOrEmpty(tpa))
                {
                    var entries = tpa.Split(Path.PathSeparator);
                    var added = 0;
                    foreach (var e in entries)
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(e) && File.Exists(e))
                            {
                                fallbackPaths.Add(e);
                                added++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Error adding TPA entry '{e}': {ex.Message}");
                        }
                    }
                    if (_verbose) Console.WriteLine($"  [TPA] Added {added} assemblies (scanner's runtime — fallback priority).");
                }
                else
                {
                    if (_verbose) Console.WriteLine("  [TPA] No Trusted Platform Assemblies found.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading TRUSTED_PLATFORM_ASSEMBLIES: {ex.Message}");
            }

            // --- Collect: scanner's runtime directory (fallback only) ---
            try
            {
                var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
                if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
                {
                    var files = GetAssemblyFiles(runtimeDir);
                    fallbackPaths.AddRange(files);
                    if (_verbose) Console.WriteLine($"  [ScannerRuntime] Added {files.Length} assemblies from scanner's runtime directory: {runtimeDir} (fallback priority).");
                }
                else
                {
                    if (_verbose) Console.WriteLine("  [ScannerRuntime] Runtime directory not found.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading scanner runtime directory assemblies: {ex.Message}");
            }

            // --- Collect: user-provided reference assemblies ---
            try
            {
                if (!string.IsNullOrEmpty(_referenceAssembliesDir) && Directory.Exists(_referenceAssembliesDir))
                {
                    var files = GetAssemblyFiles(_referenceAssembliesDir);
                    userRefPaths.AddRange(files);
                    if (_verbose) Console.WriteLine($"  [UserRef] Added {files.Length} assemblies from user-provided directory: {_referenceAssembliesDir}");
                }
                else
                {
                    if (_verbose) Console.WriteLine("  [UserRef] No user-provided reference assemblies directory configured or found.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading reference assemblies from '{_referenceAssembliesDir}': {ex.Message}");
            }

            // --- Collect: TFM reference assemblies (correct target runtime) ---
            try
            {
                if (!string.IsNullOrEmpty(detectedTfm))
                {
                    if (_verbose) Console.WriteLine($"  [TFM] Using detected target framework: {detectedTfm}");
                    var tfmFiles = FindReferenceAssembliesForTfm(detectedTfm);
                    if (tfmFiles != null && tfmFiles.Any())
                    {
                        var tfmList = tfmFiles.ToList();
                        tfmRefPaths.AddRange(tfmList);
                        if (_verbose) Console.WriteLine($"  [TFM] Added {tfmList.Count} assemblies for TFM '{detectedTfm}'.");
                    }
                    else
                    {
                        if (_verbose) Console.WriteLine($"  [TFM] No reference assemblies found for TFM '{detectedTfm}'.");
                    }
                }
                else
                {
                    if (_verbose) Console.WriteLine("  [TFM] Could not detect target framework from assembly.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error detecting target framework: {ex.Message}");
            }

            // --- Collect: assemblies next to the target assembly (local / sibling) ---
            try
            {
                var assemblyDir = Path.GetDirectoryName(assemblyPath);
                if (!string.IsNullOrEmpty(assemblyDir) && Directory.Exists(assemblyDir))
                {
                    var files = GetAssemblyFiles(assemblyDir);
                    localPaths.AddRange(files);
                    if (_verbose) Console.WriteLine($"  [Local] Added {files.Length} assemblies from target assembly directory: {assemblyDir}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading assemblies from target directory: {ex.Message}");
            }

            // --- Collect: probing privatePath from .config file (standard .NET Framework mechanism) ---
            try
            {
                var configPath = assemblyPath + ".config";
                if (File.Exists(configPath))
                {
                    var assemblyDir = Path.GetDirectoryName(assemblyPath);
                    var probingPaths = ParseProbingPrivatePaths(configPath);
                    foreach (var relativePath in probingPaths)
                    {
                        var probingDir = Path.Combine(assemblyDir!, relativePath);
                        if (Directory.Exists(probingDir))
                        {
                            var files = GetAssemblyFiles(probingDir);
                            localPaths.AddRange(files);
                            if (_verbose) Console.WriteLine($"  [Probing] Added {files.Length} assemblies from privatePath '{relativePath}': {probingDir}");
                        }
                        else
                        {
                            if (_verbose) Console.WriteLine($"  [Probing] privatePath directory not found: {probingDir}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error parsing .config probing paths: {ex.Message}");
            }

            // --- Collect: currently loaded assemblies (scanner's AppDomain — fallback) ---
            try
            {
                var added = 0;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var loc = a.Location;
                        if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                        {
                            fallbackPaths.Add(loc);
                            added++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error adding loaded assembly '{a.FullName}': {ex.Message}");
                    }
                }
                if (_verbose) Console.WriteLine($"  [AppDomain] Added {added} assemblies from scanner's AppDomain (fallback priority).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error enumerating loaded assemblies: {ex.Message}");
            }

            // --- Collect: installed .NET Core/5+ runtimes (fallback) ---
            try
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var netCoreAppDir = Path.Combine(pf, "dotnet", "shared", "Microsoft.NETCore.App");
                if (Directory.Exists(netCoreAppDir))
                {
                    var latestRuntime = Directory.GetDirectories(netCoreAppDir)
                        .OrderByDescending(d => d)
                        .FirstOrDefault();
                    if (latestRuntime != null)
                    {
                        var files = GetAssemblyFiles(latestRuntime);
                        fallbackPaths.AddRange(files);
                        if (_verbose) Console.WriteLine($"  [NetCoreFallback] Added {files.Length} assemblies from installed runtime: {latestRuntime}");
                    }
                    else
                    {
                        if (_verbose) Console.WriteLine("  [NetCoreFallback] No .NET Core runtime directories found.");
                    }
                }
                else
                {
                    if (_verbose) Console.WriteLine($"  [NetCoreFallback] .NET Core shared directory not found: {netCoreAppDir}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading .NET Core runtime assemblies: {ex.Message}");
            }

            // --- Collect: Windows Desktop (WPF/WinForms) runtime assemblies ---
            // Microsoft.WindowsDesktop.App contains full WPF assemblies (WindowsBase with
            // DependencyObject, PresentationFramework, etc.). The stubs in Microsoft.NETCore.App
            // only type-forward to these, so without them MetadataLoadContext cannot resolve WPF types.
            try
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var desktopAppDir = Path.Combine(pf, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                if (Directory.Exists(desktopAppDir))
                {
                    var latestDesktop = Directory.GetDirectories(desktopAppDir)
                        .OrderByDescending(d => d)
                        .FirstOrDefault();
                    if (latestDesktop != null)
                    {
                        var files = GetAssemblyFiles(latestDesktop);
                        // Add AFTER NETCore.App so full WPF assemblies overwrite the stubs
                        fallbackPaths.AddRange(files);
                        if (_verbose) Console.WriteLine($"  [WpfDesktop] Added {files.Length} assemblies from installed desktop runtime: {latestDesktop}");
                    }
                    else
                    {
                        if (_verbose) Console.WriteLine("  [WpfDesktop] No Windows Desktop runtime directories found.");
                    }
                }
                else
                {
                    if (_verbose) Console.WriteLine($"  [WpfDesktop] Windows Desktop shared directory not found: {desktopAppDir}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading Windows Desktop runtime assemblies: {ex.Message}");
            }

            // --- Collect: .NET Framework assemblies from Windows directory ---
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    var fwDirs = new[]
                    {
                        Path.Combine(winDir, "Microsoft.NET", "Framework64", "v4.0.30319"),
                        Path.Combine(winDir, "Microsoft.NET", "Framework", "v4.0.30319"),
                        Path.Combine(winDir, "Microsoft.NET", "Framework64", "v2.0.50727"),
                        Path.Combine(winDir, "Microsoft.NET", "Framework", "v2.0.50727"),
                    };
                    var found = false;
                    foreach (var dir in fwDirs)
                    {
                        if (Directory.Exists(dir))
                        {
                            var files = GetAssemblyFiles(dir);
                            netFxSysPaths.AddRange(files);
                            if (_verbose) Console.WriteLine($"  [NetFxSystem] Added {files.Length} assemblies from: {dir}");

                            // include subdirectories (e.g. WPF subfolder contains PresentationFramework.dll)
                            foreach (var subDir in Directory.GetDirectories(dir))
                            {
                                var subFiles = GetAssemblyFiles(subDir);
                                if (subFiles.Length > 0)
                                {
                                    netFxSysPaths.AddRange(subFiles);
                                    if (_verbose) Console.WriteLine($"  [NetFxSystem] Added {subFiles.Length} assemblies from: {subDir}");
                                }
                            }

                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        if (_verbose) Console.WriteLine("  [NetFxSystem] No .NET Framework directory found.");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error loading .NET Framework assemblies: {ex.Message}");
                }
            }

            // --- Build dedup map from lowest to highest priority (last write wins) ---
            // Priority mirrors runtime resolution order:
            //   P1 fallback (scanner's TPA/runtime/AppDomain — may not match target)
            //   P2 .NET Framework system dir (only meaningful for .NET Fx targets)
            //   P3 TFM reference assemblies (matching target's framework version)
            //   P4 user-provided --refsDir
            //   P5 local assemblies next to target (highest — like runtime probing)
            var totalCandidates = fallbackPaths.Count + netFxSysPaths.Count
                + tfmRefPaths.Count + userRefPaths.Count + localPaths.Count + 1;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // P1 (lowest): scanner's runtime + generic fallbacks
            AddToResolverMap(map, fallbackPaths);

            // P2: .NET Framework system assemblies
            // For .NET Fx targets these override scanner's .NET Core stubs;
            // for .NET Core targets they sit below TFM refs and local, so they only fill gaps.
            if (isNetFramework)
            {
                AddToResolverMap(map, netFxSysPaths);
                if (_verbose) Console.WriteLine("  [Priority] .NET Framework target: fallback < NetFx < TFM < UserRef < Local");
            }
            else
            {
                // For .NET Core targets, .NET Fx system paths are added at lowest priority
                // (below scanner fallbacks which are at least .NET Core assemblies).
                // Re-insert fallbacks after netFx to ensure .NET Core fallbacks win over .NET Fx.
                var mapBackup = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
                AddToResolverMap(map, netFxSysPaths);
                // Restore scanner fallback paths on top (they are .NET Core, more relevant)
                foreach (var kvp in mapBackup)
                    map[kvp.Key] = kvp.Value;
                if (_verbose) Console.WriteLine("  [Priority] .NET Core target: NetFx < fallback < TFM < UserRef < Local");
            }

            // P3: TFM reference assemblies (correct target runtime version)
            AddToResolverMap(map, tfmRefPaths);

            // P4: user-provided reference assemblies
            AddToResolverMap(map, userRefPaths);

            // P5 (highest): local assemblies next to target — mimics runtime probing
            AddToResolverMap(map, localPaths);

            // The analyzed assembly itself always wins
            try
            {
                var asmName = Path.GetFileName(assemblyPath);
                if (!string.IsNullOrEmpty(asmName))
                    map[asmName] = assemblyPath;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error ensuring analyzed assembly path: {ex.Message}");
            }

            if (_verbose) Console.WriteLine($"  [Summary] Total unique resolver assemblies: {map.Count} (from {totalCandidates} candidates before dedup).");
            return map.Values.ToList();
        }

        private static void AddToResolverMap(Dictionary<string, string> map, List<string> paths)
        {
            foreach (var p in paths)
            {
                try
                {
                    var name = Path.GetFileName(p);
                    if (!string.IsNullOrEmpty(name))
                        map[name] = p;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error processing path '{p}': {ex.Message}");
                }
            }
        }

        internal static bool ReferencesAssembly(string assemblyPath, string assemblySimpleName)
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata) return false;
            var reader = peReader.GetMetadataReader();
            foreach (var refHandle in reader.AssemblyReferences)
            {
                var asmRef = reader.GetAssemblyReference(refHandle);
                var name = reader.GetString(asmRef.Name);
                if (string.Equals(name, assemblySimpleName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static List<string> ParseProbingPrivatePaths(string configPath)
        {
            var result = new List<string>();
            try
            {
                var doc = new XmlDocument();
                doc.Load(configPath);
                var nsMgr = new XmlNamespaceManager(doc.NameTable);
                nsMgr.AddNamespace("asm", "urn:schemas-microsoft-com:asm.v1");
                var nodes = doc.SelectNodes("//asm:probing", nsMgr);
                if (nodes != null)
                {
                    foreach (XmlNode node in nodes)
                    {
                        var privatePath = node.Attributes?["privatePath"]?.Value;
                        if (!string.IsNullOrEmpty(privatePath))
                        {
                            foreach (var part in privatePath!.Split(';'))
                            {
                                var trimmed = part.Trim();
                                if (!string.IsNullOrEmpty(trimmed))
                                    result.Add(trimmed);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error parsing config '{configPath}': {ex.Message}");
            }
            return result;
        }

        internal static string? GetTargetFrameworkFromAssembly(string assemblyPath)
        {
            try
            {
                using var stream = File.OpenRead(assemblyPath);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata) return null;
                var reader = peReader.GetMetadataReader();
                var assemblyDef = reader.GetAssemblyDefinition();

                foreach (var handle in assemblyDef.GetCustomAttributes())
                {
                    var ca = reader.GetCustomAttribute(handle);
                    var attrType = GetAttributeTypeFullName(reader, ca);
                    if (string.Equals(attrType, "System.Runtime.Versioning.TargetFrameworkAttribute", StringComparison.Ordinal))
                    {
                        var blob = reader.GetBlobBytes(ca.Value);
                        if (blob != null && blob.Length >= 2 && blob[0] == 0x01 && blob[1] == 0x00)
                        {
                            var (value, _) = ReadSerString(blob, 2);
                            return value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error reading target framework from '{assemblyPath}': {ex.Message}");
            }

            return null;
        }

        private static string GetAttributeTypeFullName(MetadataReader reader, CustomAttribute ca)
        {
            try
            {
                var ctor = ca.Constructor;
                if (ctor.Kind == HandleKind.MemberReference)
                {
                    var mr = reader.GetMemberReference((MemberReferenceHandle)ctor);
                    var parent = mr.Parent;
                    if (parent.Kind == HandleKind.TypeReference)
                    {
                        var tr = reader.GetTypeReference((TypeReferenceHandle)parent);
                        var ns = reader.GetString(tr.Namespace);
                        var name = reader.GetString(tr.Name);
                        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                    }
                    if (parent.Kind == HandleKind.TypeDefinition)
                    {
                        var td = reader.GetTypeDefinition((TypeDefinitionHandle)parent);
                        var ns = reader.GetString(td.Namespace);
                        var name = reader.GetString(td.Name);
                        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                    }
                }
                else if (ctor.Kind == HandleKind.MethodDefinition)
                {
                    var md = reader.GetMethodDefinition((MethodDefinitionHandle)ctor);
                    var td = reader.GetTypeDefinition(md.GetDeclaringType());
                    var ns = reader.GetString(td.Namespace);
                    var name = reader.GetString(td.Name);
                    return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error reading attribute type full name: {ex.Message}");
            }

            return string.Empty;
        }

        private static (string? value, int bytesRead) ReadSerString(byte[] blob, int index)
        {
            if (index >= blob.Length) return (null, 0);
            if (blob[index] == 0xFF) return (null, 1);

            var (len, lenBytes) = ReadCompressedUInt32(blob, index);
            if (len == 0) return (string.Empty, lenBytes);
            var start = index + lenBytes;
            if (start + (int)len > blob.Length) return (null, lenBytes);
            var s = Encoding.UTF8.GetString(blob, start, (int)len);
            return (s, lenBytes + (int)len);
        }

        private static (uint value, int bytesRead) ReadCompressedUInt32(byte[] blob, int index)
        {
            if (index >= blob.Length) return (0, 0);
            byte first = blob[index];
            if ((first & 0x80) == 0)
            {
                return (first, 1);
            }
            else if ((first & 0xC0) == 0x80)
            {
                if (index + 1 >= blob.Length) return (0, 1);
                uint value = (uint)(((first & 0x3F) << 8) | blob[index + 1]);
                return (value, 2);
            }
            else if ((first & 0xE0) == 0xC0)
            {
                if (index + 3 >= blob.Length) return (0, 1);
                uint value = (uint)(((first & 0x1F) << 24) | (blob[index + 1] << 16) | (blob[index + 2] << 8) | blob[index + 3]);
                return (value, 4);
            }
            else
            {
                return (0, 1);
            }
        }

        private static IEnumerable<string> FindReferenceAssembliesForTfm(string tfm)
        {
            try
            {
                var parts = tfm.Split(',');
                var framework = parts.Length > 0 ? parts[0].Trim() : string.Empty;
                var versionPart = parts.FirstOrDefault(p => p.TrimStart().StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
                string version = string.Empty;
                if (!string.IsNullOrEmpty(versionPart))
                {
                    version = versionPart.Split('=')[1].TrimStart('v');
                }

                if (framework.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase))
                {
                    var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                    var baseDir = Path.Combine(pf86, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework");
                    if (!string.IsNullOrEmpty(version))
                    {
                        var candidate = Path.Combine(baseDir, $"v{version}");
                        if (Directory.Exists(candidate))
                            return GetAssemblyFiles(candidate);
                    }
                    if (Directory.Exists(baseDir))
                    {
                        var dirs = Directory.GetDirectories(baseDir).OrderByDescending(d => d).ToList();
                        foreach (var d in dirs)
                        {
                            var files = GetAssemblyFiles(d);
                            if (files.Length > 0) return files;
                        }
                    }
                }
                else if (framework.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase) || framework.StartsWith(".NET", StringComparison.OrdinalIgnoreCase))
                {
                    var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    var baseDir = Path.Combine(pf, "dotnet", "shared", "Microsoft.NETCore.App");
                    if (!string.IsNullOrEmpty(version))
                    {
                        var candidate = Path.Combine(baseDir, version);
                        if (Directory.Exists(candidate))
                            return GetAssemblyFiles(candidate);
                    }
                    if (Directory.Exists(baseDir))
                    {
                        var dirs = Directory.GetDirectories(baseDir).OrderByDescending(d => d).ToList();
                        foreach (var d in dirs)
                        {
                            var files = GetAssemblyFiles(d);
                            if (files.Length > 0) return files;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error finding reference assemblies for TFM '{tfm}': {ex.Message}");
            }

            return Enumerable.Empty<string>();
        }

        internal static string[] GetAssemblyFiles(string directory)
        {
            var dlls = Directory.GetFiles(directory, "*.dll");
            var exes = Directory.GetFiles(directory, "*.exe");
            var result = new string[dlls.Length + exes.Length];
            dlls.CopyTo(result, 0);
            exes.CopyTo(result, dlls.Length);
            return result;
        }
    }
}
