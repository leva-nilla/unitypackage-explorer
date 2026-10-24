namespace UnityPackageExplorer.Core;

public static class UnityZipCategorizer
{
    public static string MoveToExistingCategory(string sourcePath, string categoryRoot, string categoryName)
    {
        var root = Path.GetFullPath(categoryRoot);
        var source = Path.GetFullPath(sourcePath);
        if (!new[] { ".zip", ".rar" }.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Only ZIP and RAR files can be classified.", nameof(sourcePath));
        if (!File.Exists(source)) throw new FileNotFoundException("Archive file was not found.", source);
        if (string.IsNullOrWhiteSpace(categoryName)
            || categoryName is "." or ".."
            || categoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || categoryName.Contains('/')
            || categoryName.Contains('\\'))
            throw new ArgumentException("Category must be a single folder name.", nameof(categoryName));

        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The archive must be inside the selected category root.");

        var destinationDirectory = Path.GetFullPath(Path.Combine(root, categoryName));
        if (!destinationDirectory.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Category folder escaped the selected root.");
        if (!Directory.Exists(destinationDirectory)) throw new DirectoryNotFoundException($"Category folder was not found: {destinationDirectory}");
        if ((File.GetAttributes(destinationDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Category folder cannot be a symbolic link or junction.");

        var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
        if (string.Equals(source, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return source;

        var baseName = Path.GetFileNameWithoutExtension(source);
        var extension = Path.GetExtension(source);
        var suffix = 2;
        while (File.Exists(destination) || Directory.Exists(destination))
            destination = Path.Combine(destinationDirectory, $"{baseName} ({suffix++}){extension}");

        File.Move(source, destination);
        return destination;
    }
}
