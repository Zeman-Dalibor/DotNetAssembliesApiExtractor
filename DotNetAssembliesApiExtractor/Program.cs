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

                if (!Directory.Exists(options.ScanDir))
                {
                    Console.Error.WriteLine($"Scan directory not found: {options.ScanDir}");
                    return 2;
                }

                Directory.CreateDirectory(options.OutputDir);

                var stdoutLogPath = Path.Combine(options.OutputDir, "stdout.log");
                var stderrLogPath = Path.Combine(options.OutputDir, "stderr.log");

                using var stdoutTee = new TeeTextWriter(Console.Out, stdoutLogPath);
                using var stderrTee = new TeeTextWriter(Console.Error, stderrLogPath);
                Console.SetOut(stdoutTee);
                Console.SetError(stderrTee);

                Console.WriteLine($"Scanning: {options.ScanDir}");
                Console.WriteLine($"Output: {options.OutputDir}");
                Console.WriteLine();

                var scanner = new AssemblyScanner(options.ReferenceAssembliesDir, options.Verbose);
                var dtos = scanner.ScanDirectory(options.ScanDir);
                foreach (var dto in dtos)
                {
                    var fileName = (dto.FileName ?? "unknown") + ".json";
                    var outPath = Path.Combine(options.OutputDir, fileName);
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
