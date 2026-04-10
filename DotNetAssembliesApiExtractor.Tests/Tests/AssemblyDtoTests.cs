using System;
using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using DotNetAssembliesApiExtractor.Models;
using Xunit;

namespace DotNetAssembliesApiExtractor.Tests
{
    public class AssemblyDtoTests
    {
        [Fact]
        public void SaveAsJson_WritesFile()
        {
            var dto = new AssemblyDto
            {
                AssemblyName = "TestAssembly",
                FileName = "Test.dll",
                Types = new List<TypeDto>()
            };

            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var outFile = Path.Combine(tempDir, "out.json");
                dto.SaveAsJson(outFile);
                Assert.True(File.Exists(outFile));
                using var doc = JsonDocument.Parse(File.ReadAllText(outFile));
                Assert.Equal("TestAssembly", doc.RootElement.GetProperty("AssemblyName").GetString());
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
