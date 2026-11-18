using System.Runtime.CompilerServices;
using HelixToolkit.SharpDX.Model.Scene;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.Rendering;

/// <summary>Exact Prefab targets resolved before a scene is handed to the UI.</summary>
public sealed record ImportedModelSelection(
    IReadOnlyDictionary<MeshNode, UnityAssetReference> Materials,
    IReadOnlySet<MeshNode> ScopedMeshes,
    IReadOnlyList<PrefabMaterialOverride> UnresolvedOverrides,
    bool MeshesIsolated);

internal static class ImportedModelIdentity
{
    private static readonly ConditionalWeakTable<MeshNode, FbxModelBinding> Sources = new();

    public static void Attach(SceneNode root, IReadOnlyList<FbxModelBinding> models)
    {
        var bySource = models.GroupBy(model => (model.Path, MeshName: model.GeometryName.Length == 0 ? model.Name : model.GeometryName))
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var mesh in Meshes(root))
        {
            var names = new List<string>();
            for (var parent = mesh.Parent; parent?.Parent is not null; parent = parent.Parent)
                if (!parent.Name.Contains("_$AssimpFbx$_", StringComparison.Ordinal)) names.Add(parent.Name);
            names.Reverse();
            var path = string.Join("/", names);
            if (bySource.TryGetValue((path, mesh.Name), out var candidates) && candidates.Length == 1)
                Sources.Add(mesh, candidates[0]);
        }
    }

    public static ImportedModelSelection Resolve(SceneNode root, string modelGuid,
        IReadOnlyList<PrefabRendererMaterialAssignment> assignments, IReadOnlyList<PrefabMaterialOverride> overrides,
        ICollection<string> warnings)
    {
        var all = Meshes(root).ToArray();
        var sourceGroups = all.Where(mesh => Sources.TryGetValue(mesh, out _))
            .GroupBy(mesh => Sources.GetValue(mesh, _ => throw new InvalidOperationException())).ToArray();
        var byId = sourceGroups.GroupBy(group => UnityModelFileId.Mesh(group.Key.Name))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var selected = new HashSet<MeshNode>();
        var matchedAssignments = 0;
        foreach (var assignment in assignments)
        {
            if (!byId.TryGetValue(assignment.MeshFileId, out var targets) || targets.Length != 1) continue;
            matchedAssignments++;
            foreach (var mesh in targets[0]) selected.Add(mesh);
        }
        if (matchedAssignments > 0)
        {
            // Preserve bones/transforms, but remove unrelated geometry before framing/rendering.
            foreach (var mesh in all.Where(mesh => !selected.Contains(mesh)))
            {
                if (mesh.Parent is GroupNode parent) parent.RemoveChildNode(mesh);
                mesh.Dispose();
            }
            if (matchedAssignments != assignments.Count)
                warnings.Add($"{assignments.Count - matchedAssignments:N0} renderer Mesh fileID(s) could not be identified; their geometry was omitted instead of drawing unrelated FBX meshes.");
        }
        else selected.UnionWith(all);

        var materials = new Dictionary<MeshNode, UnityAssetReference>();
        var scoped = new HashSet<MeshNode>();
        var appliedOverrides = new HashSet<PrefabMaterialOverride>();
        foreach (var group in sourceGroups)
        {
            var candidates = assignments.Where(assignment => assignment.MeshFileId == UnityModelFileId.Mesh(group.Key.Name)).ToArray();
            var exactOwner = candidates.Length == 1 && byId[UnityModelFileId.Mesh(group.Key.Name)].Length == 1 ? candidates[0] : null;
            var rendererIds = RendererIds(group.Key.RendererPath).ToHashSet();
            var targetedOverrides = overrides.Where(item => string.Equals(item.TargetGuid, modelGuid, StringComparison.OrdinalIgnoreCase)
                && rendererIds.Contains(item.TargetFileId)).ToArray();
            // A renderer hash must identify exactly one source model, not just one imported submesh.
            targetedOverrides = targetedOverrides.Where(item => sourceGroups.Count(other => RendererIds(other.Key.RendererPath).Contains(item.TargetFileId)) == 1).ToArray();
            foreach (var mesh in group.Where(selected.Contains))
            {
                var slots = group.Key.MaterialSlots.Select((name, index) => (name, index))
                    .Where(slot => slot.name == mesh.Material?.Name).Select(slot => slot.index).ToArray();
                var slot = slots.Length == 1 ? slots[0] : -1;
                if (exactOwner is not null)
                {
                    scoped.Add(mesh);
                    if (exactOwner.Materials.Count == 1) materials[mesh] = exactOwner.Materials[0];
                    else if (slot >= 0 && exactOwner.Materials.Count == group.Key.MaterialSlots.Count)
                        materials[mesh] = exactOwner.Materials[slot];
                    else warnings.Add($"Renderer '{exactOwner.RendererName}' has no unambiguous FBX material slot for '{mesh.Name}'; no material was guessed.");
                }
                var applicable = targetedOverrides.Where(item => item.MaterialSlot == slot).ToArray();
                if (slot < 0 && targetedOverrides.Length > 0)
                {
                    scoped.Add(mesh);
                    materials.Remove(mesh);
                    warnings.Add($"FBX renderer '{group.Key.Path}' has an unresolved material slot; no override or fallback material was guessed.");
                    continue;
                }
                if (applicable.Length == 0) continue;
                scoped.Add(mesh);
                var references = applicable.Select(item => item.Material).Distinct().ToArray();
                if (references.Length != 1)
                {
                    materials.Remove(mesh);
                    continue;
                }
                materials[mesh] = references[0];
                foreach (var item in applicable) appliedOverrides.Add(item);
            }
        }
        return new ImportedModelSelection(materials, scoped, overrides.Except(appliedOverrides).ToArray(),
            assignments.Count > 0 && matchedAssignments == assignments.Count);
    }

    private static IEnumerable<long> RendererIds(string canonicalPath)
    {
        if (canonicalPath.Length == 0) yield break;
        foreach (var type in new[] { "MeshRenderer", "SkinnedMeshRenderer" })
            yield return UnityModelFileId.Renderer(canonicalPath, type);
    }

    private static IEnumerable<MeshNode> Meshes(SceneNode root)
    {
        if (root is MeshNode mesh) yield return mesh;
        foreach (var child in root.Items)
            foreach (var nested in Meshes(child)) yield return nested;
    }
}
