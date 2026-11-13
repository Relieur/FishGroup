using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// 一次性工具：把树木预制体的精细网格碰撞体换成圆台（凸包）碰撞体
// 用法：等编译完成后点「工具 → 环境 → 树木碰撞体改成圆台」，然后 Ctrl+S 保存场景
public static class TreeColliderTool
{
	const string TreePrefabDir = "Assets/coniferous_forest/Prefabs/trees";
	const string MeshDir = "Assets/coniferous_forest/Generated";

	// 圆台侧面段数，越大越圆滑，12 段够用且凸包便宜
	const int Sides = 12;
	// 顶部半径占底部半径的比例，树冠上窄下宽
	const float TopRadiusRatio = 0.18f;
	// 相对包围盒水平的收缩量，别把碰撞体做得比模型还宽
	const float RadiusScale = 0.9f;

	[MenuItem("工具/环境/树木碰撞体改成圆台")]
	public static void ReplaceTreeColliders()
	{
		string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { TreePrefabDir });
		if (guids.Length == 0)
		{
			Debug.LogError($"[树木碰撞体] {TreePrefabDir} 下没有找到预制体");
			return;
		}

		EnsureFolder(MeshDir);

		int done = 0;
		foreach (string guid in guids)
		{
			string path = AssetDatabase.GUIDToAssetPath(guid);
			if (string.IsNullOrEmpty(path) || !path.StartsWith(TreePrefabDir)) continue;
			if (ReplaceOne(path)) done++;
		}

		AssetDatabase.SaveAssets();
		EditorSceneManager.MarkAllScenesDirty();
		Debug.Log($"[树木碰撞体] 已完成 {done} 个树木预制体的圆台碰撞体，记得 Ctrl+S 保存场景。");
	}

	static bool ReplaceOne(string prefabPath)
	{
		GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
		try
		{
			if (!TryGetLocalBounds(root.transform, out Bounds bounds) || bounds.size.y < 0.01f)
			{
				Debug.LogWarning($"[树木碰撞体] {prefabPath} 找不到可用的渲染器包围盒，跳过");
				return false;
			}

			Mesh mesh = BuildFrustumMesh(prefabPath, bounds);
			if (mesh == null) return false;

			bool assigned = false;
			foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
			{
				// 只有挂在根节点上的碰撞体才能直接用根本地空间的顶点
				if (!assigned && collider.transform == root.transform)
				{
					collider.sharedMesh = mesh;
					collider.convex = true;
					collider.isTrigger = false;
					collider.enabled = true;
					assigned = true;
				}
				else
				{
					// 挂在子节点上的碰撞体没法直接复用，先关掉，统一用根节点上的那一个
					collider.enabled = false;
				}
			}

			if (!assigned)
			{
				var collider = root.GetComponent<MeshCollider>();
				if (collider == null) collider = root.AddComponent<MeshCollider>();
				collider.sharedMesh = mesh;
				collider.convex = true;
				collider.isTrigger = false;
				collider.enabled = true;
			}

			PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
			return true;
		}
		finally
		{
			PrefabUtility.UnloadPrefabContents(root);
		}
	}

	// 生成一个底面 / 顶面都封口的圆台网格，坐标直接用树木根节点的本地空间
	static Mesh BuildFrustumMesh(string prefabPath, Bounds bounds)
	{
		float height = bounds.size.y;
		float radius = Mathf.Max(bounds.size.x, bounds.size.z) * 0.5f * RadiusScale;
		if (radius < 0.02f) radius = 0.02f;
		float topRadius = radius * TopRadiusRatio;

		float bottomY = bounds.min.y;
		float topY = bounds.max.y;
		float centerX = bounds.center.x;
		float centerZ = bounds.center.z;

		var vertices = new List<Vector3>(Sides * 2 + 2);
		var triangles = new List<int>(Sides * 12);

		// 0..Sides*2-1：底边和顶边交替排列，偶数下标在底部
		for (int i = 0; i < Sides; i++)
		{
			float angle = i * Mathf.PI * 2f / Sides;
			float cos = Mathf.Cos(angle);
			float sin = Mathf.Sin(angle);
			vertices.Add(new Vector3(centerX + cos * radius, bottomY, centerZ + sin * radius));
			vertices.Add(new Vector3(centerX + cos * topRadius, topY, centerZ + sin * topRadius));
		}

		for (int i = 0; i < Sides; i++)
		{
			int next = (i + 1) % Sides;
			int b0 = i * 2, t0 = i * 2 + 1, b1 = next * 2, t1 = next * 2 + 1;
			triangles.Add(b0); triangles.Add(b1); triangles.Add(t1);
			triangles.Add(b0); triangles.Add(t1); triangles.Add(t0);
		}

		int bottomCenter = vertices.Count;
		vertices.Add(new Vector3(centerX, bottomY, centerZ));
		int topCenter = vertices.Count;
		vertices.Add(new Vector3(centerX, topY, centerZ));

		for (int i = 0; i < Sides; i++)
		{
			int next = (i + 1) % Sides;
			// 底盖朝下
			triangles.Add(bottomCenter); triangles.Add(next * 2); triangles.Add(i * 2);
			// 顶盖朝上
			triangles.Add(topCenter); triangles.Add(i * 2 + 1); triangles.Add(next * 2 + 1);
		}

		var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(prefabPath) + "_Frustum" };
		mesh.SetVertices(vertices);
		mesh.SetTriangles(triangles, 0);
		mesh.RecalculateNormals();
		mesh.RecalculateBounds();

		string meshPath = $"{MeshDir}/{mesh.name}.asset";
		if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null)
			AssetDatabase.DeleteAsset(meshPath);
		AssetDatabase.CreateAsset(mesh, meshPath);
		return AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
	}

	// 把所有渲染器的包围盒换算到 space 的本地空间（树木根节点）
	static bool TryGetLocalBounds(Transform space, out Bounds bounds)
	{
		bounds = new Bounds();
		bool has = false;
		Matrix4x4 toSpace = space.worldToLocalMatrix;

		foreach (Renderer renderer in space.GetComponentsInChildren<Renderer>(true))
		{
			Bounds local;
			if (renderer is SkinnedMeshRenderer skinned)
			{
				local = skinned.localBounds;
			}
			else
			{
				var filter = renderer.GetComponent<MeshFilter>();
				if (filter == null || filter.sharedMesh == null) continue;
				local = filter.sharedMesh.bounds;
			}

			Matrix4x4 matrix = toSpace * renderer.transform.localToWorldMatrix;
			Vector3 center = local.center;
			Vector3 extents = local.extents;

			for (int i = 0; i < 8; i++)
			{
				var corner = new Vector3(
					center.x + ((i & 1) == 0 ? -extents.x : extents.x),
					center.y + ((i & 2) == 0 ? -extents.y : extents.y),
					center.z + ((i & 4) == 0 ? -extents.z : extents.z));
				Vector3 point = matrix.MultiplyPoint3x4(corner);

				if (!has)
				{
					bounds = new Bounds(point, Vector3.zero);
					has = true;
				}
				else
				{
					bounds.Encapsulate(point);
				}
			}
		}

		return has;
	}

	static void EnsureFolder(string folder)
	{
		if (AssetDatabase.IsValidFolder(folder)) return;
		string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
		if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
		AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
	}
}
