#if UNITY_EDITOR
using System.Text;
using UnityEditor;
using UnityEngine;

// 只读诊断：分析模型网格在"立正后"的实际朝向，确定正确的前方轴
public static class InspectYangGuang
{
    [MenuItem("VRTest/Nest/Inspect Model Facing")]
    public static void Inspect()
    {
        var sb = new StringBuilder();
        sb.AppendLine("===== 模型朝向深度诊断 =====");

        var enemies = Object.FindObjectsOfType<LuffyEnemy>(true);
        if (enemies.Length == 0) { Debug.LogWarning("[Diag] 场景里没有 LuffyEnemy"); return; }

        foreach (var e in enemies)
        {
            var t = e.transform;
            sb.AppendLine("\n[" + e.name + "]");
            sb.AppendLine("  euler    = " + t.eulerAngles.ToString("F1"));
            sb.AppendLine("  forward  = " + t.forward.ToString("F3"));
            sb.AppendLine("  up       = " + t.up.ToString("F3"));
            sb.AppendLine("  right    = " + t.right.ToString("F3"));

            // 关键：看整流后的网格包围盒，判断鱼的"长轴"是哪个方向
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (r.bounds.size.sqrMagnitude < 0.000001f) continue;

                // 用世界空间的 bounds 与本地轴对比
                var localSize = r.bounds.size;
                sb.AppendLine(string.Format("  网格[{0}] 世界包围盒 size = X:{1:F2} Y:{2:F2} Z:{3:F2}",
                    r.name, localSize.x, localSize.y, localSize.z));

                // 把世界包围盒的轴转换到本地，看模型自身的长短轴
                var inv = t.rotation;
                Vector3 localX = inv * Vector3.right;
                Vector3 localY = inv * Vector3.up;
                Vector3 localZ = inv * Vector3.forward;

                float projX = Mathf.Abs(Vector3.Dot(localX, Vector3.right)) * localSize.x
                            + Mathf.Abs(Vector3.Dot(localY, Vector3.right)) * localSize.y
                            + Mathf.Abs(Vector3.Dot(localZ, Vector3.right)) * localSize.z;
                sb.AppendLine(string.Format("     本地X轴分量投影={0:F2}", projX));
            }

            // 子物体层级
            sb.AppendLine("  子物体层级:");
            PrintChildren(sb, t, 1);
        }

        Debug.Log(sb.ToString());
    }

    static void PrintChildren(StringBuilder sb, Transform parent, int depth)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            var c = parent.GetChild(i);
            if (depth > 3) continue;
            sb.AppendLine(string.Format("{0}[{1}] localEuler={2} lossyScale={3}",
                new string(' ', depth * 2), c.name, c.localEulerAngles.ToString("F1"),
                c.lossyScale.ToString("F3")));
            PrintChildren(sb, c, depth + 1);
        }
    }
}
#endif