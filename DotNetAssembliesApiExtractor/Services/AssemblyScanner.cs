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

        public AssemblyScanner(string? referenceAssembliesDir = null)
        {
            _referenceAssembliesDir = referenceAssembliesDir;
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
                    return null;
                }

                var dto = AnalyzeAssembly(filePath);
                if (dto == null)
                {
                    Console.WriteLine($"Skipping (analysis failed): {filePath}");
                    return null;
                }

                return dto;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error processing '{filePath}': {ex.Message}");
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

            // 1) user-provided reference assemblies directory
            try
            {
                if (!string.IsNullOrEmpty(_referenceAssembliesDir) && Directory.Exists(_referenceAssembliesDir))
                    paths.AddRange(Directory.GetFiles(_referenceAssembliesDir, "*.dll"));
            }
            catch { }

            // 2) try to detect target framework from the assembly and find reference assemblies
            try
            {
                var tfm = GetTargetFrameworkFromAssembly(assemblyPath);
                if (!string.IsNullOrEmpty(tfm))
                {
                    var tfmPaths = FindReferenceAssembliesForTfm(tfm);
                    if (tfmPaths != null && tfmPaths.Any())
                        paths.AddRange(tfmPaths);
                }
            }
            catch { }

            // 3) runtime directory (if present)
            try
            {
                var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
                if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
                    paths.AddRange(Directory.GetFiles(runtimeDir, "*.dll"));
            }
            catch { }

            // 4) assemblies next to the target assembly
            try
            {
                var assemblyDir = Path.GetDirectoryName(assemblyPath);
                if (!string.IsNullOrEmpty(assemblyDir) && Directory.Exists(assemblyDir))
                    paths.AddRange(Directory.GetFiles(assemblyDir, "*.dll"));
            }
            catch { }

            // 5) currently loaded assemblies (useful for single-file publish where runtime assemblies are extracted at runtime)
            try
            {
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var loc = a.Location;
                        if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                            paths.Add(loc);
                    }
                    catch { }
                }
            }
            catch { }

            // 6) ensure the analyzed assembly itself is present
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
                catch { }
            }

            // Ensure the assembly being analyzed is present and wins for its file name
            try
            {
                var asmName = Path.GetFileName(assemblyPath);
                if (!string.IsNullOrEmpty(asmName))
                    map[asmName] = assemblyPath;
            }
            catch { }

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
            catch { }

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
            catch { }

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
                            return Directory.GetFiles(candidate, "*.dll");
                    }
                    if (Directory.Exists(baseDir))
                    {
                        var dirs = Directory.GetDirectories(baseDir).OrderByDescending(d => d).ToList();
                        foreach (var d in dirs)
                        {
                            var files = Directory.GetFiles(d, "*.dll");
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
                            return Directory.GetFiles(candidate, "*.dll");
                    }
                    if (Directory.Exists(baseDir))
                    {
                        var dirs = Directory.GetDirectories(baseDir).OrderByDescending(d => d).ToList();
                        foreach (var d in dirs)
                        {
                            var files = Directory.GetFiles(d, "*.dll");
                            if (files.Length > 0) return files;
                        }
                    }
                }
            }
            catch { }

            return Enumerable.Empty<string>();
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
