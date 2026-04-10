using System;
using System.IO;
using System.Linq;
using Xunit;
using DotNetAssembliesApiExtractor.Services;

namespace DotNetAssembliesApiExtractor.Tests
{
    public class SampleAssembliesScanTests
    {
        [Fact]
        public void Scan_PublishedSampleAssemblies_ReturnsDto()
        {
            var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "samples"));
            Assert.True(Directory.Exists(baseDir), $"Samples directory not found: {baseDir}");

            var scanner = new AssemblyScanner(null);

            var sampleDirs = Directory.GetDirectories(baseDir);
            Assert.NotEmpty(sampleDirs);

            foreach (var dir in sampleDirs)
            {
                var dtos = scanner.ScanDirectory(dir).ToList();
                Assert.NotEmpty(dtos);
            }
        }
    }
}
