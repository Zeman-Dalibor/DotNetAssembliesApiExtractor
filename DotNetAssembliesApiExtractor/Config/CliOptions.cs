using System;
using System.Diagnostics.CodeAnalysis;

namespace DotNetAssembliesApiExtractor.Config
{
    internal sealed class CliOptions
    {
        public string ScanDir { get; }
        public string OutputDir { get; }
        public string? ReferenceAssembliesDir { get; }

        private CliOptions(string scanDir, string outputDir, string? referenceAssembliesDir)
        {
            ScanDir = scanDir;
            OutputDir = outputDir;
            ReferenceAssembliesDir = referenceAssembliesDir;
        }

        public static bool TryParse(string[] args, [NotNullWhen(true)] out CliOptions? options)
        {
            options = null;
            string? scanDir = null;
            string? outputDir = null;
            string? refsDir = null;

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--scanDir", StringComparison.OrdinalIgnoreCase))
                {
                    if (a.Contains('='))
                        scanDir = a.Split('=', 2)[1].Trim('"');
                    else if (i + 1 < args.Length)
                        scanDir = args[++i];
                }
                else if (a.StartsWith("--outputDir", StringComparison.OrdinalIgnoreCase))
                {
                    if (a.Contains('='))
                        outputDir = a.Split('=', 2)[1].Trim('"');
                    else if (i + 1 < args.Length)
                        outputDir = args[++i];
                }
                else if (a.StartsWith("--refsDir", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--refs-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (a.Contains('='))
                        refsDir = a.Split('=', 2)[1].Trim('"');
                    else if (i + 1 < args.Length)
                        refsDir = args[++i];
                }
                else if (a == "-h" || a == "--help")
                {
                    options = null;
                    return false;
                }
            }

            if (string.IsNullOrEmpty(scanDir) || string.IsNullOrEmpty(outputDir))
                return false;

            options = new CliOptions(scanDir!, outputDir!, refsDir);
            return true;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("Usage: DotNetAssembliesApiExtractor --scanDir <dir> --outputDir <dir> [--refsDir <dir>]");
        }
    }
}
