#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

// 一次性工具：把 BossNest 脚本挂到场景里的 BossNest 空物体上并配置好
public static class SetupBossNest
{
    static readonly string[] ModelPaths =
    {
        "Assets/LuffyEffect/YG_01/Prefabs/YG_02.prefab",
        "Assets/LuffyEffect/YG_01/Prefabs/YG_03.prefab",
        "Assets/LuffyEffect/YG_01/Prefabs/YG_04.prefab",
        "Assets/LuffyEffect/YG_01/Prefabs/YG_05.prefab",
    };

    [MenuItem("VRTest/Boss/Setup Boss Nest")]
    public static void Setup()
    {
        var bossType = System.Type.GetType("VRTest.Environment.BossNest, Assembly-CSharp");
        if (bossType == null) { Debug.LogError("[Boss] 找不到 BossNest，请先等编译完成"); return; }

        // 找场景里的 BossNest 空物体
        var go = GameObject.Find("BossNest");
        if (go == null)
        {
            Debug.LogError("[Boss] 场景里找不到名为 BossNest 的对象");
            return;
        }

        // 已有组件就先移除，避免重复
        var existing = go.GetComponent(bossType);
        if (existing != null) Object.DestroyImmediate(existing);

        var comp = go.AddComponent(bossType);
        var so = new SerializedObject(comp);

        // 随机挑一个模型
        var valid = new List<GameObject>();
        foreach (var path in ModelPaths)
        {
            var m = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (m != null) valid.Add(m);
        }

        if (valid.Count == 0) { Debug.LogError("[Boss] 没有可用模型"); return; }
        var picked = valid[Random.Range(0, valid.Count)];

        var prefabProp = so.FindProperty("bossPrefab");
        if (prefabProp != null) prefabProp.objectReferenceValue = picked;
        else Debug.LogError("[Boss] 缺字段 bossPrefab");

        // 配置参数
        SetF(so, "bossScale", 4f);
        SetF(so, "bossHealth", 100f);
        SetF(so, "bossTurnSpeed", 1.5f);
        SetF(so, "bubbleInterval", 2.5f);
        SetF(so, "bubbleSpeed", 8f);
        SetF(so, "bubbleScale", 3f);
        SetF(so, "bubbleDamage", 10f);
        SetF(so, "activateRadius", 18f);
        SetBool(so, "drawGizmos", true);

        var euler = so.FindProperty("spawnEuler");
        if (euler != null) euler.vector3Value = new Vector3(270f, 270f, 0f);

        so.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(go);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
        AssetDatabase.SaveAssets();

        Debug.Log(string.Format("[Boss] 已配置 BossNest\n  模型: {0}\n  位置: {1}\n  缩放: 4x  血量: 100",
            picked.name, go.transform.position));
    }

    static void SetF(SerializedObject so, string n, float v)
    {
        var p = so.FindProperty(n);
        if (p != null) p.floatValue = v; else Debug.LogWarning("[Boss] 缺字段: " + n);
    }

    static void SetInt(SerializedObject so, string n, int v)
    {
        var p = so.FindProperty(n);
        if (p != null) p.intValue = v; else Debug.LogWarning("[Boss] 缺字段: " + n);
    }

    static void SetBool(SerializedObject so, string n, bool v)
    {
        var p = so.FindProperty(n);
        if (p != null) p.boolValue = v; else Debug.LogWarning("[Boss] 缺字段: " + n);
    }
}
#endif