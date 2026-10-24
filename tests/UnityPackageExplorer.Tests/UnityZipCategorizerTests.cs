using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class UnityZipCategorizerTests
{
    [Fact]
    public void MovesIntoAnExistingCategoryWithoutOverwritingNameCollisions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-category-test-{Guid.NewGuid():N}");
        var category = Path.Combine(root, "衣装");
        Directory.CreateDirectory(category);
        var source = Path.Combine(root, "item.zip");
        var existing = Path.Combine(category, "item.zip");
        File.WriteAllText(source, "source contents");
        File.WriteAllText(existing, "keep existing contents");
        try
        {
            var moved = UnityZipCategorizer.MoveToExistingCategory(source, root, "衣装");

            Assert.Equal(Path.Combine(category, "item (2).zip"), moved);
            Assert.False(File.Exists(source));
            Assert.Equal("source contents", File.ReadAllText(moved));
            Assert.Equal("keep existing contents", File.ReadAllText(existing));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingCategoryAndEscapingPathsLeaveTheSourceInPlace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-category-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "item.zip");
        File.WriteAllText(source, "keep");
        try
        {
            Assert.Throws<DirectoryNotFoundException>(() => UnityZipCategorizer.MoveToExistingCategory(source, root, "missing"));
            Assert.Throws<ArgumentException>(() => UnityZipCategorizer.MoveToExistingCategory(source, root, ".."));
            Assert.Throws<InvalidOperationException>(() => UnityZipCategorizer.MoveToExistingCategory(source, Path.Combine(root, "other"), "missing"));
            Assert.Equal("keep", File.ReadAllText(source));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MovesRarIntoAnExistingCategory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"unitypackage-category-test-{Guid.NewGuid():N}");
        var category = Path.Combine(root, "衣装");
        Directory.CreateDirectory(category);
        var source = Path.Combine(root, "item.rar");
        File.WriteAllText(source, "source contents");
        try
        {
            var moved = UnityZipCategorizer.MoveToExistingCategory(source, root, "衣装");

            Assert.Equal(Path.Combine(category, "item.rar"), moved);
            Assert.False(File.Exists(source));
            Assert.Equal("source contents", File.ReadAllText(moved));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
