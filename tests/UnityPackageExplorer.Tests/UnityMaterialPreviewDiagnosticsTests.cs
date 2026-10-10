using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Tests;

public sealed class UnityMaterialPreviewDiagnosticsTests
{
    [Fact]
    public void ExplainsWhenPrefabHasNoMaterialReferencesWithoutAssumingTexturesAreAbsent()
    {
        var warning = UnityMaterialPreviewDiagnostics.GetMissingSupportedTextureWarning(0);

        Assert.Contains("Unity Material 参照がありません", warning);
        Assert.Contains("FBX 内または隣接フォルダの画像を Importer が読み込めた場合", warning);
        Assert.Contains("Unity 固有の材質・シェーダー設定は再現されません", warning);
    }

    [Fact]
    public void ExplainsWhenReferencedMaterialsHaveNoSupportedColorTexture()
    {
        var warning = UnityMaterialPreviewDiagnostics.GetMissingSupportedTextureWarning(10);

        Assert.Contains("Unity Material 参照 10 件", warning);
        Assert.Contains("プレビュー対応の色テクスチャがありません", warning);
        Assert.Contains("FBX 内または隣接フォルダの画像を Importer が読み込めた場合", warning);
    }

    [Fact]
    public void RejectsNegativeMaterialReferenceCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UnityMaterialPreviewDiagnostics.GetMissingSupportedTextureWarning(-1));
    }
}
