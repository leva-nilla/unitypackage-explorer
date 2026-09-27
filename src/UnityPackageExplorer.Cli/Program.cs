using UnityPackageExplorer.Core;

if (args.Length != 3 || !string.Equals(args[0], "extract", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: UnityPackageExplorer.Cli extract <package.unitypackage> <destination-base>");
    return 2;
}

try
{
    var packagePath = Path.GetFullPath(args[1]);
    if (!string.Equals(Path.GetExtension(packagePath), ".unitypackage", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Specify a .unitypackage file.", nameof(args));

    Console.WriteLine(new UnityPackageReader(packagePath).ExtractTo(args[2]));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
