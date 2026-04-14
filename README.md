# DotNetAssembliesApiExtractor
A tool that scans all .NET assemblies in a directory and extracts their APIs into JSON files.

## Quick start

1. Build the project:

```powershell
dotnet build DotNetAssembliesApiExtractor -c Release
```

2. Run using `dotnet run` (from the repository root):

```powershell
dotnet run --project DotNetAssembliesApiExtractor -- --scanDir TestAssemblies/Net6/bin/Debug/net6.0 --outputDir artifacts/samples/net6
```

Note: `--` separates arguments for `dotnet` from the application arguments.

3. Or publish and run the produced binary:

```powershell
dotnet publish DotNetAssembliesApiExtractor -c Release -o publish
publish\DotNetAssembliesApiExtractor.exe --scanDir "C:\path\to\assemblies" --outputDir "C:\path\to\out"
```
or use `publish.bat` file.

## Usage (CLI)

Usage: DotNetAssembliesApiExtractor.exe --scanDir <dir> --outputDir <dir> [--refsDir <dir>] [--verbose]

- `--scanDir` (required): folder to recursively scan for assemblies (.dll/.exe)
- `--outputDir` (required): folder where resulting JSON files will be written (one JSON per assembly)
- `--refsDir` (optional): folder with reference assemblies (helps resolving dependencies, e.g. for .NET Framework or specific TFMs)
- `--verbose` (optional): enables detailed diagnostic output (resolver paths, TFM detection, assembly counts, etc.)

## Examples

1) Scan local test assemblies (if built):

```powershell
dotnet run --project DotNetAssembliesApiExtractor -- --scanDir TestAssemblies/Net6/bin/Release/net6.0 --outputDir artifacts/samples/net6
```

2) Scan any folder using reference assemblies:

```powershell
dotnet run --project DotNetAssembliesApiExtractor -- --scanDir "C:\MyAssemblies" --outputDir "C:\MyOut" --refsDir "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8"
```

## Output (JSON)

For each analyzed assembly a JSON file is created with fields `AssemblyName`, `FileName` and `Types`. Short sample:

```json
{
	"AssemblyName": "TestAssemblies.Net6, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
	"FileName": "TestAssemblies.Net6.dll",
	"Types": [
		{
			"FullName": "TestTypes",
			"Namespace": null,
			"Kind": "Class",
			"Methods": [
				{
					"Name": "Method1",
					"ReturnType": "System.Void",
					"IsPublic": true,
					"IsStatic": false,
					"IsAbstract": false,
					"IsVirtual": false,
					"Parameters": []
				}
			]
		}
	]
}
```

## Exit codes

- `0` — success
- `1` — invalid arguments (prints usage)
- `2` — `--scanDir` not found
- `99` — unhandled error

## Notes

- The tool uses `MetadataLoadContext` and several fallback strategies to locate references. If analysis fails, try providing `--refsDir` with reference assemblies.
- For .NET Framework assemblies it's often necessary to set `--refsDir` to the Reference Assemblies folder on Windows.
