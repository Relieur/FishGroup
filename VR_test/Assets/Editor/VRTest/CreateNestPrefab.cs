#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// 一次性工具：把 EnemyNest 做成预制体，并用预制体实例重建场景里的巢穴
public static class CreateNestPrefab
{
    const string Folder = "Assets/Prefabs";
    const string PrefabPath = "Assets/Prefabs/EnemyNest.prefab";

    static readonly string[] ModelPaths =
    {
        "Assets/LuffyEffect/YG_01/Prefabs/YG_02.prefab",
        "Assets/LuffyEffect/YG_01/Prefabs/YG_03.prefab",
        "Assets/LuffyEffect/YG_01/Prefabs/YG_04.prefab",
        "Assets/LuffyEffect/YG_01/Prefabs/YG_05.prefab",
    };

    // 场景里两处巢穴的位置
    static readonly Vector3[] SpawnPositions =
    {
        new Vector3(-22f, 0f, -18f),
        new Vector3(24f, 0f, 20f),
    };
    static readonly string[] SpawnNames = { "EnemyNest_A", "EnemyNest_B" };

    [MenuItem("VRTest/Nest/1. Create Nest Prefab")]
    public static void CreatePrefab()
    {
        var prefab = BuildPrefab();
        if (prefab != null) Debug.Log("[Nest] 预制体已创建: " + PrefabPath);
    }

    [MenuItem("VRTest/Nest/2. Place Nest Instances")]
    public static void PlaceInstances()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogWarning("[Nest] 预制体不存在，先执行 1. Create Nest Prefab");
            prefab = BuildPrefab();
            if (prefab == null) return;
        }

        // 清掉旧的巢穴（不管是普通对象还是实例）
        foreach (var n in SpawnNames)
        {
            var old = GameObject.Find(n);
            if (old != null) Object.DestroyImmediate(old);
        }

        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        var created = new List<GameObject>();

        for (int i = 0; i < SpawnPositions.Length; i++)
        {
            Vector3 pos = SnapToGround(SpawnPositions[i]);

            // 用 PrefabUtility 实例化，保证是预制体实例（改预制体会同步）
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            inst.name = SpawnNames[i];
            inst.transform.position = pos;

            created.Add(inst);
            Debug.Log(string.Format("[Nest] 已放置 {0} 于 {1}", SpawnNames[i], pos));
        }

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.SaveAssets();

        if (created.Count >= 2)
        {
            Debug.Log(string.Format("[Nest] 两处巢穴距离: {0:F1} 米",
                Vector3.Distance(created[0].transform.position, created[1].transform.position)));
        }
    }

    [MenuItem("VRTest/Nest/Create Prefab And Place All")]
    public static void CreateAndPlace()
    {
        CreatePrefab();
        PlaceInstances();
    }

    static GameObject BuildPrefab()
    {
        var nestType = System.Type.GetType("VRTest.Environment.EnemyNest, Assembly-CSharp");
        if (nestType == null) { Debug.LogError("[Nest] 找不到 EnemyNest，请先等编译完成"); return null; }

        if (!AssetDatabase.IsValidFolder(Folder))
        {
            AssetDatabase.CreateFolder("Assets", "Prefabs");
            Debug.Log("[Nest] 已创建目录 " + Folder);
        }

        var temp = new GameObject("EnemyNest");
        var comp = temp.AddComponent(nestType);
        var so = new SerializedObject(comp);

        var listProp = so.FindProperty("enemyPrefabs");
        if (listProp != null)
        {
            var valid = new List<GameObject>();
            foreach (var path in ModelPaths)
            {
                var m = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (m != null) valid.Add(m);
                else Debug.LogWarning("[Nest] 缺模型: " + path);
            }

            listProp.arraySize = valid.Count;
            for (int i = 0; i < valid.Count; i++)
                listProp.GetArrayElementAtIndex(i).objectReferenceValue = valid[i];
        }

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

        var saved = PrefabUtility.SaveAsPrefabAsset(temp, PrefabPath);
        Object.DestroyImmediate(temp);

        if (saved == null) { Debug.LogError("[Nest] 预制体创建失败"); return null; }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return saved;
    }

    static Vector3 SnapToGround(Vector3 p)
    {
        if (Physics.Raycast(p + Vector3.up * 20f, Vector3.down, out RaycastHit hit,
                60f, ~0, QueryTriggerInteraction.Ignore))
        {
            return new Vector3(p.x, hit.point.y + 0.05f, p.z);
        }
        return p;
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