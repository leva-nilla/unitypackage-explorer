namespace UnityPackageExplorer.Core;

public static class UnityMaterialPreviewDiagnostics
{
    public static string GetMissingSupportedTextureWarning(int materialReferenceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(materialReferenceCount);

        var reason = materialReferenceCount == 0
            ? "Prefab に Unity Material 参照がありません。"
            : $"Prefab の Unity Material 参照 {materialReferenceCount:N0} 件に、プレビュー対応の色テクスチャがありません。";

        return $"{reason} FBX 内または隣接フォルダの画像を Importer が読み込めた場合は表示されることがあります。Unity 固有の材質・シェーダー設定は再現されません。";
    }
}
