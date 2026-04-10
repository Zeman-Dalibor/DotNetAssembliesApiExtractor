using System;
using System.IO;
using System.Linq;
using Xunit;
using DotNetAssembliesApiExtractor.Services;

namespace DotNetAssembliesApiExtractor.Tests
{
    public class AssemblyScannerTests
    {
        [Fact]
        public void ScanDirectory_ReturnsDto_ForAssemblyFile()
        {
            var sourceAssembly = typeof(AssemblyScanner).Assembly.Location;
            var tempDir = Path.Combine(Path.GetTempPath(), "DotNetAssembliesApiExtractorTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var dest = Path.Combine(tempDir, Path.GetFileName(sourceAssembly));
            File.Copy(sourceAssembly, dest, true);

            try
            {
                var scanner = new AssemblyScanner(null);
                var dtos = scanner.ScanDirectory(tempDir).ToList();

                Assert.NotEmpty(dtos);
                var dto = dtos.First();
                Assert.Equal(Path.GetFileName(sourceAssembly), dto.FileName);
                Assert.False(string.IsNullOrEmpty(dto.AssemblyName));
                Assert.NotNull(dto.Types);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
