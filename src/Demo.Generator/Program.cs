using System.Reflection;
using Demo.Generator;
using Demo.Tasks;

// Usage: output file, optional task assembly, optional generated namespace.
if (args.Length is < 1 or > 3) throw new ArgumentException("Supply output.cs [tasks.dll] [namespace].");
var assembly = args.Length > 1 ? Assembly.LoadFrom(Path.GetFullPath(args[1])) : typeof(IAppTask<,>).Assembly;
var code = AdapterGenerator.Generate(assembly.GetTypes(), args.Length > 2 ? args[2] : "Demo.Elsa.Generated");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
File.WriteAllText(args[0], code);
Console.WriteLine($"Generated adapters from {assembly.GetName().Name} → {args[0]}");
