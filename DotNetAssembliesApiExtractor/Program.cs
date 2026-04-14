using System;
using System.IO;
using DotNetAssembliesApiExtractor.Config;
using DotNetAssembliesApiExtractor.Services;

namespace DotNetAssembliesApiExtractor
{
    internal class Program
    {
        static int Main(string[] args)
        {
            try
            {
                if (!CliOptions.TryParse(args, out var options))
                {
                    CliOptions.PrintUsage();
                    return 1;
                }

                var scanDir = Path.GetFullPath(options.ScanDir);
                var outputDir = Path.GetFullPath(options.OutputDir);

                if (!Directory.Exists(scanDir))
                {
                    Console.Error.WriteLine($"Scan directory not found: {scanDir}");
                    return 2;
                }

                Directory.CreateDirectory(outputDir);

                var stdoutLogPath = Path.Combine(outputDir, "stdout.log");
                var stderrLogPath = Path.Combine(outputDir, "stderr.log");

                using var stdoutTee = new TeeTextWriter(Console.Out, stdoutLogPath);
                using var stderrTee = new TeeTextWriter(Console.Error, stderrLogPath);
                Console.SetOut(stdoutTee);
                Console.SetError(stderrTee);

                Console.WriteLine($"Scanning: {scanDir}");
                Console.WriteLine($"Output: {outputDir}");
                Console.WriteLine();

                var scanner = new AssemblyScanner(options.ReferenceAssembliesDir, options.Verbose);
                var dtos = scanner.ScanDirectory(scanDir);
                foreach (var dto in dtos)
                {
                    var fileName = (dto.FileName ?? "unknown") + ".json";
                    var outPath = Path.Combine(outputDir, fileName);
                    dto.SaveAsJson(outPath);
                    Console.WriteLine($"Wrote: {outPath}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unhandled error: {ex}");
                return 99;
            }
        }
    }
}
