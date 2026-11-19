using System;
using UnityEngine;

/// <summary>
/// Coral_03 的 FBX 里自带 LOD0~LOD2 三档网格（CoralA_LOD0/1/2，由 FBX 的 LODBox 组导入成 LODGroup）。
/// 远处会自动切到低模，低模的轮廓、面片划分和 LOD0 不一样，看起来像换了个模型，
/// 低模上还容易出现整块发黑的三角面。
///
/// 这里在场景加载后只处理 Coral_03（含 Coral_03 (1) 这类副本）：
/// 删掉 LOD1、LOD2 的渲染物体，并把 LODGroup 关掉、手动只打开 LOD0，
/// 免得 LODGroup 继续按屏幕占比切换或在远处把物体剔掉。
/// </summary>
static class LODLevelLock
{
    const string TargetName = "Coral_03";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void StripLowerLods()
    {
        LODGroup[] groups = UnityEngine.Object.FindObjectsByType<LODGroup>(FindObjectsSortMode.None);
        foreach (LODGroup group in groups)
        {
            if (!IsTarget(group.gameObject)) continue;

            LOD[] lods = group.GetLODs();
            Renderer[] lod0 = lods[0].renderers;

            for (int i = 1; i < lods.Length; i++)
            {
                foreach (Renderer renderer in lods[i].renderers)
                {
                    // 只删 LOD 自己的子物体，避免误删 LODGroup 所在的那层
                    if (renderer == null || renderer.gameObject == group.gameObject) continue;

                    renderer.enabled = false;
                    UnityEngine.Object.Destroy(renderer.gameObject);
                }
            }

            group.enabled = false;

            foreach (Renderer renderer in lod0)
            {
                if (renderer == null) continue;
                renderer.gameObject.SetActive(true);
                renderer.enabled = true;
            }
        }
    }

    static bool IsTarget(GameObject go)
    {
        for (Transform t = go.transform; t != null; t = t.parent)
        {
            if (t.name.StartsWith(TargetName, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
