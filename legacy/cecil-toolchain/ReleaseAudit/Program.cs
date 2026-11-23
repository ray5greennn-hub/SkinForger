using Mono.Cecil;

if (args.Length == 0)
    throw new ArgumentException("Usage: ReleaseAudit <file-or-directory> [...]");

foreach (var path in args.SelectMany(Expand).Order(StringComparer.OrdinalIgnoreCase))
{
    try
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { InMemory = true });
        Console.WriteLine($"FILE={path}");
        Console.WriteLine($"ASSEMBLY={assembly.Name.Name} VERSION={assembly.Name.Version}");
        foreach (var attribute in assembly.CustomAttributes.Where(a =>
                     a.AttributeType.FullName is "MelonLoader.MelonInfoAttribute" or
                         "MelonLoader.MelonGameAttribute" or
                         "System.Reflection.AssemblyTitleAttribute" or
                         "System.Reflection.AssemblyProductAttribute" or
                         "System.Reflection.AssemblyCompanyAttribute" or
                         "System.Reflection.AssemblyDescriptionAttribute"))
        {
            var values = string.Join(" | ", attribute.ConstructorArguments.Select(a => a.Value?.ToString() ?? "<null>"));
            Console.WriteLine($"ATTR={attribute.AttributeType.FullName}: {values}");
        }
        Console.WriteLine("TYPES=" + string.Join(", ", assembly.MainModule.Types.Where(t => t.Name != "<Module>").Select(t => t.FullName).Take(12)));
        Console.WriteLine("REFS=" + string.Join(", ", assembly.MainModule.AssemblyReferences.Select(r => r.Name).Order()));
    }
    catch (BadImageFormatException)
    {
        Console.WriteLine($"FILE={path}\nNOT_MANAGED");
    }
    Console.WriteLine();
}

static IEnumerable<string> Expand(string path)
{
    if (File.Exists(path))
        return new[] { Path.GetFullPath(path) };
    if (Directory.Exists(path))
        return Directory.GetFiles(path, "*.dll", SearchOption.TopDirectoryOnly).Select(Path.GetFullPath);
    throw new FileNotFoundException(path);
}
