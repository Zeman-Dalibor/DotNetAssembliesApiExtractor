using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DotNetAssembliesApiExtractor.Models;

namespace DotNetAssembliesApiExtractor.Services
{
    internal class AssemblyScanner
    {
        private readonly string? _referenceAssembliesDir;
        private readonly bool _verbose;

        public AssemblyScanner(string? referenceAssembliesDir = null, bool verbose = false)
        {
            _referenceAssembliesDir = referenceAssembliesDir;
            _verbose = verbose;
        }

        public IEnumerable<Models.AssemblyDto> ScanDirectory(string scanDir)
        {
            foreach (var file in Directory.EnumerateFiles(scanDir, "*", SearchOption.AllDirectories))
            {
                var dto = ProcessFile(file);
                if (dto != null)
                    yield return dto;
            }
        }

        private Models.AssemblyDto? ProcessFile(string filePath)
        {
            try
            {
                if (!IsDotNetAssembly(filePath))
                {
                    Console.WriteLine($"Skipping non-.NET file: {filePath}");
                    Console.WriteLine();
                    return null;
                }

                var dto = AnalyzeAssembly(filePath);
                if (dto == null)
                {
                    Console.WriteLine($"Skipping (analysis failed): {filePath}");
                    Console.WriteLine();
                    return null;
                }

                return dto;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error processing '{filePath}': {ex.Message}");
                Console.WriteLine();
                return null;
            }
        }

        private bool IsDotNetAssembly(string path)
        {
            try
            {
                AssemblyName.GetAssemblyName(path);
                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
            catch (FileLoadException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private AssemblyDto? AnalyzeAssembly(string assemblyPath)
        {
            try
            {
                var resolverList = CollectResolverPaths(assemblyPath);
                var resolver = new PathAssemblyResolver(resolverList);
                using var mlc = new MetadataLoadContext(resolver);

                var assembly = mlc.LoadFromAssemblyPath(assemblyPath);
                var dto = new AssemblyDto
                {
                    AssemblyName = assembly.FullName,
                    FileName = Path.GetFileName(assemblyPath),
                    Types = new List<TypeDto>()
                };

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException rtlex)
                {
                    types = rtlex.Types.Where(t => t != null).ToArray()!;
                }

                foreach (var t in types)
                {
                    if (t == null) continue;
                    var typeDto = new TypeDto
                    {
                        FullName = t.FullName,
                        Namespace = t.Namespace,
                        Kind = GetTypeKind(t),
                        Methods = new List<MethodDto>()
                    };

                    var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    foreach (var m in methods)
                    {
                        var methodDto = new MethodDto
                        {
                            Name = m.Name,
                            ReturnType = m.ReturnType?.FullName,
                            IsPublic = m.IsPublic,
                            IsStatic = m.IsStatic,
                            IsAbstract = m.IsAbstract,
                            IsVirtual = m.IsVirtual,
                            Parameters = m.GetParameters().Select(p => new ParameterDto { Name = p.Name, Type = p.ParameterType?.FullName }).ToList()
                        };
                        typeDto.Methods.Add(methodDto);
                    }

                    dto.Types.Add(typeDto);
                }

                return dto;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Analysis failed for '{assemblyPath}': {ex.Message}");
                return null;
            }
        }

        private List<string> CollectResolverPaths(string assemblyPath)
        {
            var paths = new List<string>();
            if (_verbose) Console.WriteLine($"Collecting resolver paths for: {assemblyPath}");

            // 0) include trusted platform assemblies (TPA) so the core assembly can be resolved
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
                                paths.Add(e);
                                added++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Error adding TPA entry '{e}': {ex.Message}");
                        }
                    }
                    if (_verbose) Console.WriteLine($"  [TPA] Added {added} assemblies from Trusted Platform Assemblies.");
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

            // 1) user-provided reference assemblies directory
            try
            {
                if (!string.IsNullOrEmpty(_referenceAssembliesDir) && Directory.Exists(_referenceAssembliesDir))
                {
                    var files = GetAssemblyFiles(_referenceAssembliesDir);
                    paths.AddRange(files);
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

            // 2) try to detect target framework from the assembly and find reference assemblies
            try
            {
                var tfm = GetTargetFrameworkFromAssembly(assemblyPath);
                if (!string.IsNullOrEmpty(tfm))
                {
                    if (_verbose) Console.WriteLine($"  [TFM] Detected target framework: {tfm}");
                    var tfmPaths = FindReferenceAssembliesForTfm(tfm);
                    if (tfmPaths != null && tfmPaths.Any())
                    {
                        var tfmList = tfmPaths.ToList();
                        paths.AddRange(tfmList);
                        if (_verbose) Console.WriteLine($"  [TFM] Added {tfmList.Count} assemblies for TFM '{tfm}'.");
                    }
                    else
                    {
                        if (_verbose) Console.WriteLine($"  [TFM] No reference assemblies found for TFM '{tfm}'.");
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

            // 3) runtime directory (if present)
            try
            {
                var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
                if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
                {
                    var files = GetAssemblyFiles(runtimeDir);
                    paths.AddRange(files);
                    if (_verbose) Console.WriteLine($"  [Runtime] Added {files.Length} assemblies from runtime directory: {runtimeDir}");
                }
                else
                {
                    if (_verbose) Console.WriteLine("  [Runtime] Runtime directory not found.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading runtime directory assemblies: {ex.Message}");
            }

            // 4) assemblies next to the target assembly
            try
            {
                var assemblyDir = Path.GetDirectoryName(assemblyPath);
                if (!string.IsNullOrEmpty(assemblyDir) && Directory.Exists(assemblyDir))
                {
                    var files = GetAssemblyFiles(assemblyDir);
                    paths.AddRange(files);
                    if (_verbose) Console.WriteLine($"  [SiblingDir] Added {files.Length} assemblies from target assembly directory: {assemblyDir}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error loading assemblies from target directory: {ex.Message}");
            }

            // 5) currently loaded assemblies (useful for single-file publish where runtime assemblies are extracted at runtime)
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
                            paths.Add(loc);
                            added++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error adding loaded assembly '{a.FullName}': {ex.Message}");
                    }
                }
                if (_verbose) Console.WriteLine($"  [AppDomain] Added {added} assemblies from currently loaded AppDomain.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error enumerating loaded assemblies: {ex.Message}");
            }

            // 6) fallback: installed .NET Core/5+ runtimes (essential for single-file publish where TPA/runtime dir are unavailable)
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
                        paths.AddRange(files);
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

            // 7) fallback: .NET Framework assemblies from Windows directory (for mscorlib.dll, WPF, etc. when analyzing .NET Framework assemblies)
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
                            paths.AddRange(files);
                            if (_verbose) Console.WriteLine($"  [NetFxFallback] Added {files.Length} assemblies from: {dir}");

                            // include subdirectories (e.g. WPF subfolder contains PresentationFramework.dll)
                            foreach (var subDir in Directory.GetDirectories(dir))
                            {
                                var subFiles = GetAssemblyFiles(subDir);
                                if (subFiles.Length > 0)
                                {
                                    paths.AddRange(subFiles);
                                    if (_verbose) Console.WriteLine($"  [NetFxFallback] Added {subFiles.Length} assemblies from: {subDir}");
                                }
                            }

                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        if (_verbose) Console.WriteLine("  [NetFxFallback] No .NET Framework directory found.");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error loading .NET Framework assemblies: {ex.Message}");
                }
            }

            // 8) ensure the analyzed assembly itself is present
            paths.Add(assemblyPath);

            // dedupe by file name (assembly simple name) to avoid loading same identity twice
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                try
                {
                    var name = Path.GetFileName(p);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!map.ContainsKey(name))
                    {
                        map[name] = p;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error processing path '{p}': {ex.Message}");
                }
            }

            // Ensure the assembly being analyzed is present and wins for its file name
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

            if (_verbose) Console.WriteLine($"  [Summary] Total unique resolver assemblies: {map.Count} (from {paths.Count} candidates before dedup).");
            return map.Values.ToList();
        }

        private static string? GetTargetFrameworkFromAssembly(string assemblyPath)
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
            // 0xFF indicates null
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
                // Expecting something like: ".NETCoreApp,Version=v3.1" or ".NETFramework,Version=v4.7.2"
                var parts = tfm.Split(',');
                var framework = parts.Length > 0 ? parts[0].Trim() : string.Empty;
                var versionPart = parts.FirstOrDefault(p => p.TrimStart().StartsWith("Version=", StringComparison.OrdinalIgnoreCase));
                string version = string.Empty;
                if (!string.IsNullOrEmpty(versionPart))
                {
                    version = versionPart.Split('=')[1].TrimStart('v');
                }

                if (framework.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase) || framework.StartsWith(".NET", StringComparison.OrdinalIgnoreCase))
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
                else if (framework.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase))
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
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error finding reference assemblies for TFM '{tfm}': {ex.Message}");
            }

            return Enumerable.Empty<string>();
        }

        private static string[] GetAssemblyFiles(string directory)
        {
            var dlls = Directory.GetFiles(directory, "*.dll");
            var exes = Directory.GetFiles(directory, "*.exe");
            var result = new string[dlls.Length + exes.Length];
            dlls.CopyTo(result, 0);
            exes.CopyTo(result, dlls.Length);
            return result;
        }

        private static string GetTypeKind(Type t)
        {
            if (t.IsClass) return "Class";
            if (t.IsInterface) return "Interface";
            if (t.IsEnum) return "Enum";
            if (t.IsValueType) return "Struct";
            return "Unknown";
        }
    }
}
