using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DotNetAssembliesApiExtractor.Models;

namespace DotNetAssembliesApiExtractor.Services
{
    /// <summary>
    /// Analyzes a single .NET assembly using MetadataLoadContext.
    /// Extracts types, methods, and parameters into DTOs.
    /// </summary>
    internal class AssemblyAnalyzer
    {
        private readonly AssemblyReferenceResolver _resolver;

        public AssemblyAnalyzer(AssemblyReferenceResolver resolver)
        {
            _resolver = resolver;
        }

        public AssemblyDto? Analyze(string assemblyPath)
        {
            try
            {
                var resolverList = _resolver.CollectResolverPaths(assemblyPath);
                var pathResolver = new PathAssemblyResolver(resolverList);
                using var mlc = new MetadataLoadContext(pathResolver);

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

                    MethodInfo[] methods;
                    try
                    {
                        methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  Warning: cannot enumerate methods for type '{t.FullName}': {ex.Message}");
                        methods = Array.Empty<MethodInfo>();
                    }

                    foreach (var m in methods)
                    {
                        try
                        {
                            var methodDto = new MethodDto
                            {
                                Name = m.Name,
                                ReturnType = SafeGetTypeName(() => m.ReturnType),
                                IsPublic = m.IsPublic,
                                IsStatic = m.IsStatic,
                                IsAbstract = m.IsAbstract,
                                IsVirtual = m.IsVirtual,
                                Parameters = SafeGetParameters(m)
                            };
                            typeDto.Methods.Add(methodDto);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"  Warning: skipping method '{m.Name}' in type '{t.FullName}': {ex.Message}");
                        }
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

        private static string? SafeGetTypeName(Func<Type?> typeAccessor)
        {
            try
            {
                return typeAccessor()?.FullName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static List<ParameterDto> SafeGetParameters(MethodInfo method)
        {
            ParameterInfo[] parameters;
            try
            {
                parameters = method.GetParameters();
            }
            catch (Exception)
            {
                return new List<ParameterDto>();
            }

            var result = new List<ParameterDto>(parameters.Length);
            foreach (var p in parameters)
            {
                string? typeName = null;
                try
                {
                    typeName = p.ParameterType?.FullName;
                }
                catch (Exception)
                {
                    // Type resolution failed (e.g. circular type forwarding) — leave as null
                }
                result.Add(new ParameterDto { Name = p.Name, Type = typeName });
            }
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
