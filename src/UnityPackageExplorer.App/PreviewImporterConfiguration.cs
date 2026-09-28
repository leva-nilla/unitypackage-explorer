using HelixToolkit.SharpDX.Assimp;
using HelixToolkit.SharpDX.Model;

namespace UnityPackageExplorer.App;

internal static class PreviewImporterConfiguration
{
    public static ImporterConfiguration Create() => new()
    {
        ImportMaterialType = MaterialType.BlinnPhong,
        CreateSkeletonForBoneSkinningMesh = true
    };
}
