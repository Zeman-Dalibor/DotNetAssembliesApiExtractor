using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DotNetAssembliesApiExtractor.Models;

namespace DotNetAssembliesApiExtractor.Services
{
    internal class AssemblyScanner
    {
        private readonly AssemblyReferenceResolver _resolver;
        private readonly AssemblyAnalyzer _analyzer;

        public AssemblyScanner(string? referenceAssembliesDir = null, bool verbose = false)
        {
            _resolver = new AssemblyReferenceResolver(referenceAssembliesDir, verbose);
            _analyzer = new AssemblyAnalyzer(_resolver);
        }

        public IEnumerable<AssemblyDto> ScanDirectory(string scanDir)
        {
            foreach (var file in Directory.EnumerateFiles(scanDir, "*", SearchOption.AllDirectories))
            {
                var dto = ProcessFile(file);
                if (dto != null)
                {
                    yield return dto;
                }
            }
        }

        public IEnumerable<string> EnumerateAssemblyFiles(string scanDir)
        {
            foreach (var file in Directory.EnumerateFiles(scanDir, "*", SearchOption.AllDirectories))
            {
                if (!IsDotNetAssembly(file))
                {
                    Console.WriteLine($"Skipping non-.NET file: {file}");
                    Console.WriteLine();
                    continue;
                }
                yield return file;
            }
        }

        public AssemblyDto? ProcessFile(string filePath)
        {
            try
            {
                if (!IsDotNetAssembly(filePath))
                {
                    Console.WriteLine($"Skipping non-.NET file: {filePath}");
                    Console.WriteLine();
                    return null;
                }

                var dto = _analyzer.Analyze(filePath);
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

        public static bool IsDotNetAssembly(string path)
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
    }
}
