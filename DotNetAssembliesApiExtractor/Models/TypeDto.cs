using System.Collections.Generic;

namespace DotNetAssembliesApiExtractor.Models
{
    internal class TypeDto
    {
        public string? FullName { get; set; }
        public string? Namespace { get; set; }
        public string? Kind { get; set; }
        public List<MethodDto>? Methods { get; set; }
    }
}
