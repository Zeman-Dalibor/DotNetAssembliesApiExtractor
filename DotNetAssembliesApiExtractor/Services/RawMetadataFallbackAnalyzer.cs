using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using DotNetAssembliesApiExtractor.Models;

namespace DotNetAssembliesApiExtractor.Services
{
    /// <summary>
    /// Fallback analyzer using raw System.Reflection.Metadata to extract type and method
    /// information without MetadataLoadContext. This avoids StackOverflowException caused
    /// by circular type forwarding in MetadataLoadContext's recursive type resolution.
    /// Trade-off: parameter types and return types are decoded on a best-effort basis
    /// (simple types only; complex generics may appear as raw signature tokens).
    /// </summary>
    internal static class RawMetadataFallbackAnalyzer
    {
        public static AssemblyDto? Analyze(string assemblyPath, bool verbose)
        {
            try
            {
                if (verbose) Console.WriteLine($"[RawMetadata] Fallback analysis for: {assemblyPath}");

                using var stream = File.OpenRead(assemblyPath);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata) return null;

                var reader = peReader.GetMetadataReader();
                var assemblyDef = reader.GetAssemblyDefinition();
                var assemblyName = FormatAssemblyName(reader, assemblyDef);

                var dto = new AssemblyDto
                {
                    AssemblyName = assemblyName,
                    FileName = Path.GetFileName(assemblyPath),
                    Types = new List<TypeDto>()
                };

                foreach (var typeHandle in reader.TypeDefinitions)
                {
                    var typeDef = reader.GetTypeDefinition(typeHandle);
                    var ns = reader.GetString(typeDef.Namespace);
                    var name = reader.GetString(typeDef.Name);

                    // Skip the special <Module> type
                    if (string.IsNullOrEmpty(ns) && name == "<Module>") continue;

                    var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                    var kind = GetTypeKind(typeDef.Attributes);

                    var typeDto = new TypeDto
                    {
                        FullName = fullName,
                        Namespace = ns,
                        Kind = kind,
                        Methods = new List<MethodDto>()
                    };

                    foreach (var methodHandle in typeDef.GetMethods())
                    {
                        try
                        {
                            var methodDef = reader.GetMethodDefinition(methodHandle);
                            var methodName = reader.GetString(methodDef.Name);
                            var attrs = methodDef.Attributes;

                            var (returnType, paramTypes) = DecodeMethodSignature(reader, methodDef);
                            var parameters = BuildParameterList(reader, methodDef, paramTypes);

                            var methodDto = new MethodDto
                            {
                                Name = methodName,
                                ReturnType = returnType,
                                IsPublic = (attrs & MethodAttributes.MemberAccessMask) == MethodAttributes.Public,
                                IsStatic = (attrs & MethodAttributes.Static) != 0,
                                IsAbstract = (attrs & MethodAttributes.Abstract) != 0,
                                IsVirtual = (attrs & MethodAttributes.Virtual) != 0,
                                Parameters = parameters
                            };
                            typeDto.Methods.Add(methodDto);
                        }
                        catch (Exception ex)
                        {
                            if (verbose) Console.Error.WriteLine($"  [RawMetadata] Warning: skipping method in '{fullName}': {ex.Message}");
                        }
                    }

                    dto.Types.Add(typeDto);
                }

                if (verbose) Console.WriteLine($"[RawMetadata] Extracted {dto.Types.Count} types from {assemblyPath}");
                return dto;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RawMetadata] Failed for '{assemblyPath}': {ex.Message}");
                return null;
            }
        }

        private static string FormatAssemblyName(MetadataReader reader, AssemblyDefinition def)
        {
            var name = reader.GetString(def.Name);
            var version = def.Version;
            var culture = reader.GetString(def.Culture);
            if (string.IsNullOrEmpty(culture)) culture = "neutral";
            var pkt = def.PublicKey.IsNil ? "null" : FormatPublicKeyToken(reader.GetBlobBytes(def.PublicKey));
            return $"{name}, Version={version}, Culture={culture}, PublicKeyToken={pkt}";
        }

        private static string FormatPublicKeyToken(byte[] publicKey)
        {
            // Public key → public key token (SHA1 hash, last 8 bytes reversed)
            // For simplicity, if the key is already 8 bytes it's the token; otherwise compute
            if (publicKey.Length == 0) return "null";
            if (publicKey.Length == 8)
                return BitConverter.ToString(publicKey).Replace("-", "").ToLowerInvariant();

            using var sha1 = System.Security.Cryptography.SHA1.Create();
            var hash = sha1.ComputeHash(publicKey);
            var token = new byte[8];
            for (int i = 0; i < 8; i++)
                token[i] = hash[hash.Length - 1 - i];
            return BitConverter.ToString(token).Replace("-", "").ToLowerInvariant();
        }

        private static string GetTypeKind(TypeAttributes attrs)
        {
            if ((attrs & TypeAttributes.Interface) != 0) return "Interface";
            if ((attrs & TypeAttributes.ClassSemanticsMask) == TypeAttributes.Class)
            {
                // Could be enum or struct — check for sealed+value type heuristically
                // We don't have base type info easily, so use class as default
                return "Class";
            }
            return "Unknown";
        }

        private static (string? returnType, string?[] paramTypes) DecodeMethodSignature(MetadataReader reader, MethodDefinition methodDef)
        {
            try
            {
                var sig = methodDef.DecodeSignature(new SimpleTypeProvider(reader), null);
                return (sig.ReturnType, sig.ParameterTypes.ToArray());
            }
            catch
            {
                return (null, Array.Empty<string?>());
            }
        }

        private static List<ParameterDto> BuildParameterList(MetadataReader reader, MethodDefinition methodDef, string?[] paramTypes)
        {
            var result = new List<ParameterDto>();
            var paramHandles = methodDef.GetParameters();
            var paramIndex = 0;

            foreach (var ph in paramHandles)
            {
                var param = reader.GetParameter(ph);
                // Sequence 0 = return value, skip it
                if (param.SequenceNumber == 0) continue;

                var name = reader.GetString(param.Name);
                var typeIndex = param.SequenceNumber - 1;
                string? typeName = (typeIndex >= 0 && typeIndex < paramTypes.Length) ? paramTypes[typeIndex] : null;

                result.Add(new ParameterDto { Name = name, Type = typeName });
                paramIndex++;
            }

            return result;
        }

        /// <summary>
        /// A simple ISignatureTypeProvider that decodes signature blobs into human-readable type strings.
        /// Handles common cases (primitives, simple type refs); returns raw info for complex generics.
        /// </summary>
        private sealed class SimpleTypeProvider : ISignatureTypeProvider<string?, object?>
        {
            private readonly MetadataReader _reader;

            public SimpleTypeProvider(MetadataReader reader) => _reader = reader;

            public string? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
            {
                PrimitiveTypeCode.Void => "System.Void",
                PrimitiveTypeCode.Boolean => "System.Boolean",
                PrimitiveTypeCode.Char => "System.Char",
                PrimitiveTypeCode.SByte => "System.SByte",
                PrimitiveTypeCode.Byte => "System.Byte",
                PrimitiveTypeCode.Int16 => "System.Int16",
                PrimitiveTypeCode.UInt16 => "System.UInt16",
                PrimitiveTypeCode.Int32 => "System.Int32",
                PrimitiveTypeCode.UInt32 => "System.UInt32",
                PrimitiveTypeCode.Int64 => "System.Int64",
                PrimitiveTypeCode.UInt64 => "System.UInt64",
                PrimitiveTypeCode.Single => "System.Single",
                PrimitiveTypeCode.Double => "System.Double",
                PrimitiveTypeCode.String => "System.String",
                PrimitiveTypeCode.Object => "System.Object",
                PrimitiveTypeCode.IntPtr => "System.IntPtr",
                PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
                PrimitiveTypeCode.TypedReference => "System.TypedReference",
                _ => typeCode.ToString()
            };

            public string? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            {
                var td = reader.GetTypeDefinition(handle);
                var ns = reader.GetString(td.Namespace);
                var name = reader.GetString(td.Name);
                return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            }

            public string? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                var tr = reader.GetTypeReference(handle);
                var ns = reader.GetString(tr.Namespace);
                var name = reader.GetString(tr.Name);
                return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            }

            public string? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            {
                var spec = reader.GetTypeSpecification(handle);
                try
                {
                    return spec.DecodeSignature(this, genericContext);
                }
                catch
                {
                    return null;
                }
            }

            public string? GetSZArrayType(string? elementType) => elementType + "[]";
            public string? GetArrayType(string? elementType, ArrayShape shape) => elementType + $"[{new string(',', shape.Rank - 1)}]";
            public string? GetByReferenceType(string? elementType) => elementType + "&";
            public string? GetPointerType(string? elementType) => elementType + "*";
            public string? GetPinnedType(string? elementType) => elementType;
            public string? GetModifiedType(string? modifier, string? unmodifiedType, bool isRequired) => unmodifiedType;

            public string? GetGenericInstantiation(string? genericType, System.Collections.Immutable.ImmutableArray<string?> typeArguments)
            {
                var sb = new StringBuilder();
                sb.Append(genericType);
                sb.Append('<');
                for (int i = 0; i < typeArguments.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(typeArguments[i] ?? "?");
                }
                sb.Append('>');
                return sb.ToString();
            }

            public string? GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
            public string? GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
            public string? GetFunctionPointerType(MethodSignature<string?> signature) => "fnptr";
        }
    }
}
