using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class UnityMaterialNameMatcherTests
{
    [Fact]
    public void MatchesSmallImporterNameDifferenceAndFullWidthPunctuation()
    {
        Assert.True(UnityMaterialNameMatcher.AreEquivalent(
            "Overalls_sio_shirt＆shoes",
            "Overalls_shirt&shoes"));
    }

    [Fact]
    public void MatchesTextureFilenameStemToImportedMaterialName()
    {
        Assert.True(UnityMaterialNameMatcher.AreEquivalent("bandageH", "bandage_H"));
        Assert.True(UnityMaterialNameMatcher.AreEquivalent("innerPants", "innrePants"));
        Assert.True(UnityMaterialNameMatcher.AreEquivalent("scarf", "scarf.001"));
    }

    [Fact]
    public void MatchesExactNamesIgnoringCasePunctuationAndUnityInstanceSuffix()
    {
        Assert.True(UnityMaterialNameMatcher.AreEquivalent("scarf.001", "SCARF"));
        Assert.True(UnityMaterialNameMatcher.AreEquivalent("Some-Material (Instance)", "some_material"));
    }

    [Fact]
    public void DoesNotFuzzyMatchGenericOrShortMaterialNames()
    {
        Assert.False(UnityMaterialNameMatcher.AreEquivalent("Material", "Overalls"));
        Assert.False(UnityMaterialNameMatcher.AreEquivalent("bandage", "bandage_H"));
        Assert.False(UnityMaterialNameMatcher.AreEquivalent("Material", "Matrial"));
    }

    [Fact]
    public void EmptyNamesDoNotMatch()
    {
        Assert.False(UnityMaterialNameMatcher.AreEquivalent(null, "Material"));
        Assert.False(UnityMaterialNameMatcher.AreEquivalent("   ", "   "));
    }

    [Fact]
    public void FuzzyComparisonIsBoundedButLongExactNamesStillMatch()
    {
        var atLimit = new string('a', 128);
        var atLimitWithTypo = atLimit[..^1] + "b";
        var overLimit = new string('a', 129);
        var overLimitWithTypo = overLimit[..^1] + "b";

        Assert.True(UnityMaterialNameMatcher.AreEquivalent(atLimit, atLimitWithTypo));
        Assert.False(UnityMaterialNameMatcher.AreEquivalent(overLimit, overLimitWithTypo));
        Assert.True(UnityMaterialNameMatcher.AreEquivalent(overLimit, overLimit));
    }
}
