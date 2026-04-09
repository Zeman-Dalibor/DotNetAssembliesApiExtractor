using System.Collections.Generic;

namespace DotNetAssembliesApiExtractor.Models
{
    internal class MethodDto
    {
        public string? Name { get; set; }
        public string? ReturnType { get; set; }
        public bool IsPublic { get; set; }
        public bool IsStatic { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsVirtual { get; set; }
        public List<ParameterDto>? Parameters { get; set; }
    }
}
