#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

// 一次性：在场景里放置两处 EnemyNest，使用 YG_02~05。执行后删除本脚本。
public static class SetupNests
{
    static readonly string[] ModelPaths =
    {
        "Assets/LuffyEffect/YG_01/Prefabs/YG_02.prefab",
        "Assets/LuffyEffect/YG_01/Prefabs/YG_03.prefab",
        "Assets/LuffyEffect/YG_01/Prefabs/YG_04.prefab",
        "Assets/LuffyEffect/YG_01/Prefabs/YG_05.prefab",
    };

    [MenuItem("VRTest/Nest/Create Two Nests")]
    public static void Create()
    {
        var nestType = System.Type.GetType("VRTest.Environment.EnemyNest, Assembly-CSharp");
        if (nestType == null) { Debug.LogError("[Nest] 找不到 EnemyNest，请先等编译完成"); return; }

        var models = new GameObject[ModelPaths.Length];
        for (int i = 0; i < ModelPaths.Length; i++)
        {
            models[i] = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPaths[i]);
            if (models[i] == null) Debug.LogWarning("[Nest] 缺模型: " + ModelPaths[i]);
        }

        var a = CreateNest("EnemyNest_A", new Vector3(-22f, 0f, -18f), nestType, models);
        var b = CreateNest("EnemyNest_B", new Vector3(24f, 0f, 20f), nestType, models);

        SnapToGround(a);
        SnapToGround(b);

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        AssetDatabase.SaveAssets();

        Debug.Log(string.Format("[Nest] 已创建两处巢穴（YG_02~05）\n  A: {0}\n  B: {1}\n  距离: {2:F1} 米",
            a.transform.position, b.transform.position,
            Vector3.Distance(a.transform.position, b.transform.position)));
    }

    static GameObject CreateNest(string name, Vector3 pos, System.Type nestType, GameObject[] models)
    {
        var old = GameObject.Find(name);
        if (old != null) Object.DestroyImmediate(old);

        var go = new GameObject(name);
        go.transform.position = pos;

        var comp = go.AddComponent(nestType);
        var so = new SerializedObject(comp);

        // 模型列表
        var listProp = so.FindProperty("enemyPrefabs");
        if (listProp != null)
        {
            int valid = 0;
            for (int i = 0; i < models.Length; i++) if (models[i] != null) valid++;

            listProp.arraySize = valid;
            int k = 0;
            for (int i = 0; i < models.Length; i++)
            {
                if (models[i] == null) continue;
                listProp.GetArrayElementAtIndex(k).objectReferenceValue = models[i];
                k++;
            }
        }
        else Debug.LogWarning("[Nest] 缺字段: enemyPrefabs");

        SetInt(so, "minCount", 3);
        SetInt(so, "maxCount", 5);
        SetF(so, "respawnDelay", 5f);
        SetF(so, "spawnRadius", 3.5f);
        SetF(so, "alertRadius", 16f);
        SetF(so, "aggroRadius", 9f);
        SetF(so, "leaveRadius", 20f);
        SetF(so, "alertSpeedScale", 0.45f);
        SetBool(so, "drawGizmos", true);

        var euler = so.FindProperty("spawnEuler");
        if (euler != null) euler.vector3Value = new Vector3(270f, 270f, 0f);

        so.ApplyModifiedPropertiesWithoutUndo();
        return go;
    }

    static void SnapToGround(GameObject go)
    {
        if (Physics.Raycast(go.transform.position + Vector3.up * 20f, Vector3.down,
                out RaycastHit hit, 60f, ~0, QueryTriggerInteraction.Ignore))
        {
            go.transform.position = new Vector3(
                go.transform.position.x, hit.point.y + 0.05f, go.transform.position.z);
        }
    }

    static void SetF(SerializedObject so, string n, float v)
    {
        var p = so.FindProperty(n);
        if (p != null) p.floatValue = v; else Debug.LogWarning("[Nest] 缺字段: " + n);
    }

    static void SetInt(SerializedObject so, string n, int v)
    {
        var p = so.FindProperty(n);
        if (p != null) p.intValue = v; else Debug.LogWarning("[Nest] 缺字段: " + n);
    }

    static void SetBool(SerializedObject so, string n, bool v)
    {
        var p = so.FindProperty(n);
        if (p != null) p.boolValue = v; else Debug.LogWarning("[Nest] 缺字段: " + n);
    }
}
#endif