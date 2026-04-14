using System;
using System.IO;
using System.Text;

namespace DotNetAssembliesApiExtractor.Services
{
    /// <summary>
    /// A <see cref="TextWriter"/> that writes to both a file and the original console stream.
    /// </summary>
    internal sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly StreamWriter _file;

        public TeeTextWriter(TextWriter consoleWriter, string filePath)
        {
            _console = consoleWriter ?? throw new ArgumentNullException(nameof(consoleWriter));
            _file = new StreamWriter(filePath, append: false, encoding: Encoding.UTF8) { AutoFlush = true };
        }

        public override Encoding Encoding => _console.Encoding;

        public override void Write(char value)
        {
            _console.Write(value);
            _file.Write(value);
        }

        public override void Write(string? value)
        {
            _console.Write(value);
            _file.Write(value);
        }

        public override void WriteLine(string? value)
        {
            _console.WriteLine(value);
            _file.WriteLine(value);
        }

        public override void WriteLine()
        {
            _console.WriteLine();
            _file.WriteLine();
        }

        public override void Flush()
        {
            _console.Flush();
            _file.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
