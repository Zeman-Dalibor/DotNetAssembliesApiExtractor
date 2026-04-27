using System;
using System.Diagnostics;
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

                var outputDir = Path.GetFullPath(options.OutputDir);
                Directory.CreateDirectory(outputDir);

                // --singleFile mode: analyze one assembly, write JSON, exit.
                // Used by the parent process for crash isolation.
                if (!string.IsNullOrEmpty(options.SingleFile))
                {
                    return RunSingleFile(options.SingleFile!, outputDir, options);
                }

                var scanDir = Path.GetFullPath(options.ScanDir);

                if (!Directory.Exists(scanDir))
                {
                    Console.Error.WriteLine($"Scan directory not found: {scanDir}");
                    return 2;
                }

                var stdoutLogPath = Path.Combine(outputDir, "stdout.log");
                var stderrLogPath = Path.Combine(outputDir, "stderr.log");

                using var stdoutTee = new TeeTextWriter(Console.Out, stdoutLogPath);
                using var stderrTee = new TeeTextWriter(Console.Error, stderrLogPath);
                Console.SetOut(stdoutTee);
                Console.SetError(stderrTee);

                Console.WriteLine($"Scanning: {scanDir}");
                Console.WriteLine($"Output: {outputDir}");
                Console.WriteLine();

                var selfExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                var isDotnetRun = selfExe != null && Path.GetFileNameWithoutExtension(selfExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

                var scanner = new AssemblyScanner(options.ReferenceAssembliesDir, options.Verbose);

                foreach (var filePath in scanner.EnumerateAssemblyFiles(scanDir))
                {
                    var fileName = Path.GetFileName(filePath);
                    var outPath = Path.Combine(outputDir, fileName + ".json");

                    if (File.Exists(outPath))
                    {
                        Console.WriteLine($"Already exists, skipping: {outPath}");
                        Console.WriteLine();
                        continue;
                    }

                    // Spawn a child process to isolate StackOverflowException crashes
                    var exitCode = RunChildProcess(selfExe, isDotnetRun, filePath, outputDir, options, rawMetadata: false);

                    // If MetadataLoadContext failed (any non-zero exit), retry with raw MetadataReader fallback
                    if (exitCode != 0)
                    {
                        Console.Error.WriteLine($"MetadataLoadContext analysis failed (exit code {exitCode}) for: {filePath} — retrying with raw MetadataReader...");
                        exitCode = RunChildProcess(selfExe, isDotnetRun, filePath, outputDir, options, rawMetadata: true);
                    }

                    if (exitCode == 0)
                    {
                        Console.WriteLine($"Wrote: {outPath}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"Child process failed (exit code {exitCode}) for: {filePath}");
                    }
                    Console.WriteLine();
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unhandled error: {ex}");
                return 99;
            }
        }

        private static int RunSingleFile(string filePath, string outputDir, CliOptions options)
        {
            Models.AssemblyDto? dto;

            if (options.RawMetadata)
            {
                // Raw MetadataReader path — avoids MetadataLoadContext's recursive type resolution
                dto = RawMetadataFallbackAnalyzer.Analyze(filePath, options.Verbose);
            }
            else
            {
                var scanner = new AssemblyScanner(options.ReferenceAssembliesDir, options.Verbose);
                dto = scanner.ProcessFile(filePath);
            }

            if (dto == null)
                return 3;

            var fileName = (dto.FileName ?? "unknown") + ".json";
            var outPath = Path.Combine(outputDir, fileName);
            dto.SaveAsJson(outPath);
            return 0;
        }

        private static int RunChildProcess(string? selfExe, bool isDotnetRun, string filePath, string outputDir, CliOptions options, bool rawMetadata)
        {
            var childArgs = $"--singleFile \"{filePath}\" --outputDir \"{outputDir}\"";
            if (!string.IsNullOrEmpty(options.ReferenceAssembliesDir))
                childArgs += $" --refsDir \"{options.ReferenceAssembliesDir}\"";
            if (options.Verbose)
                childArgs += " --verbose";
            if (rawMetadata)
                childArgs += " --rawMetadata";

            ProcessStartInfo psi;
            if (isDotnetRun)
            {
                // When running via 'dotnet run', we need to find the actual built DLL
                var assemblyLocation = typeof(Program).Assembly.Location;
                psi = new ProcessStartInfo("dotnet", $"\"{assemblyLocation}\" {childArgs}");
            }
            else
            {
                psi = new ProcessStartInfo(selfExe!, childArgs);
            }

            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) return -1;

                // Read both streams asynchronously to avoid deadlock
                var stdoutTask = System.Threading.Tasks.Task.Run(() => proc.StandardOutput.ReadToEnd());
                var stderrTask = System.Threading.Tasks.Task.Run(() => proc.StandardError.ReadToEnd());

                proc.WaitForExit(60_000);

                if (!proc.HasExited)
                {
                    proc.Kill();
                    Console.Error.WriteLine($"Child process timed out (60s) for: {filePath}");
                    return -2;
                }

                var stdout = stdoutTask.Result;
                var stderr = stderrTask.Result;

                if (!string.IsNullOrEmpty(stdout))
                    Console.Write(stdout);

                if (!string.IsNullOrEmpty(stderr))
                {
                    // Truncate massive StackOverflow traces to avoid flooding logs
                    if (proc.ExitCode != 0 && stderr.Length > 2000)
                    {
                        Console.Error.Write(stderr.Substring(0, 500));
                        Console.Error.WriteLine($"\n  ... [{stderr.Length} chars truncated] ...");
                    }
                    else
                    {
                        Console.Error.Write(stderr);
                    }
                }

                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to spawn child process for '{filePath}': {ex.Message}");
                return -1;
            }
        }
    }
}
