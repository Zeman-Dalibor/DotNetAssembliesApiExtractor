using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DotNetAssembliesApiExtractor.Models
{
    internal class AssemblyDto
    {
        public string? AssemblyName { get; set; }
        public string? FileName { get; set; }
        public List<TypeDto>? Types { get; set; }

        public void SaveAsJson(string path)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}
