using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

// 一次性工具：把鱼群用的旧沙丁鱼模型换成 Assets/沙丁鱼xxx.fbx 里的新模型
// 用法：等编译完成后点「工具 → 鱼群 → 用 沙丁鱼xxx 替换旧沙丁鱼」，然后 Ctrl+S 保存场景
public static class ReplaceSardineModelTool
{
	const string FbxPath = "Assets/沙丁鱼xxx.fbx";
	const string OldPrefabPath = "Assets/JSAllAnimals/Vertebrata/Fishes/Sardine/Prefabs/Ver1.2.1/SardineSkinWithController.prefab";
	const string BackupPath = "Assets/JSAllAnimals/Vertebrata/Fishes/Sardine/Prefabs/Ver1.2.1/SardineSkinWithController_OldModel.prefab";
	const string ControllerPath = "Assets/JSAllAnimals/Vertebrata/Fishes/Sardine/Prefabs/Ver1.2.1/SardineNew.controller";
	const string MaterialDir = "Assets/JSAllAnimals/Vertebrata/Fishes/Sardine/Materials/NewSardine";

	// 旧鱼体长约 0.55 米，新模型导入后按这个长度等比缩放
	const float TargetLength = 0.55f;

	[MenuItem("工具/鱼群/用 沙丁鱼xxx 替换旧沙丁鱼")]
	public static void ReplaceModel()
	{
		GameObject fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
		if (fbx == null)
		{
			Debug.LogError($"[沙丁鱼] 找不到 {FbxPath}");
			return;
		}
		if (AssetDatabase.LoadAssetAtPath<GameObject>(OldPrefabPath) == null)
		{
			Debug.LogError($"[沙丁鱼] 找不到旧预制体 {OldPrefabPath}");
			return;
		}

		// 1. 让模型生成 Avatar，并给所有动画剪辑打开循环
		if (!PrepareFbx(out Avatar avatar, out AnimationClip fastSwimClip, out AnimationClip idleClip))
			return;

		// 2. 备份旧预制体（已存在就不覆盖）
		if (AssetDatabase.LoadAssetAtPath<GameObject>(BackupPath) == null)
		{
			AssetDatabase.CopyAsset(OldPrefabPath, BackupPath);
			Debug.Log($"[沙丁鱼] 旧预制体已备份到 {BackupPath}");
		}

		// 3. 建 AnimatorController：默认 FastSwim，拿起时切 Idle
		AnimatorController controller = BuildController(fastSwimClip, idleClip);

		// 4. 修改预制体：只替换可视模型，保留刚体 / 抓取 / 脚本 / 碰撞体
		GameObject root = PrefabUtility.LoadPrefabContents(OldPrefabPath);
		try
		{
			SwapVisual(root, fbx, avatar, controller, out string detail);
			PrefabUtility.SaveAsPrefabAsset(root, OldPrefabPath);
			Debug.Log("[沙丁鱼] 替换完成：" + detail);
		}
		finally
		{
			PrefabUtility.UnloadPrefabContents(root);
		}

		AssetDatabase.SaveAssets();
		EditorSceneManager.MarkAllScenesDirty();
		Debug.Log("[沙丁鱼] 5 个鱼群已自动更新，记得 Ctrl+S 保存场景。旧模型可用备份预制体还原。");
	}

	// 导入设置：Generic + 由模型创建 Avatar，剪辑全部设为循环
	static bool PrepareFbx(out Avatar avatar, out AnimationClip fastSwimClip, out AnimationClip idleClip)
	{
		avatar = null;
		fastSwimClip = null;
		idleClip = null;

		var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
		if (importer == null)
		{
			Debug.LogError($"[沙丁鱼] {FbxPath} 不是模型资产");
			return false;
		}

		bool dirty = false;
		if (importer.animationType != ModelImporterAnimationType.Generic)
		{
			importer.animationType = ModelImporterAnimationType.Generic;
			dirty = true;
		}
		if (importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
		{
			importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
			dirty = true;
		}

		ModelImporterClipAnimation[] takes = importer.defaultClipAnimations;
		if (takes != null && takes.Length > 0 && takes.Any(t => !t.loopTime))
		{
			foreach (ModelImporterClipAnimation take in takes)
			{
				take.loopTime = true;
				take.loopPose = true;
			}
			importer.clipAnimations = takes;
			dirty = true;
		}

		if (dirty) importer.SaveAndReimport();

		Object[] assets = AssetDatabase.LoadAllAssetsAtPath(FbxPath);
		avatar = assets.OfType<Avatar>().FirstOrDefault();
		if (avatar == null)
		{
			Debug.LogError("[沙丁鱼] 模型没有生成 Avatar，无法播放动画。请到模型 Inspector 的 Rig 页确认 Animation Type 为 Generic、Avatar Definition 为 Create From This Model。");
			return false;
		}

		List<AnimationClip> clips = assets.OfType<AnimationClip>()
			.Where(c => !c.name.StartsWith("__preview__"))
			.ToList();
		if (clips.Count == 0)
		{
			Debug.LogError("[沙丁鱼] 模型里没有找到动画剪辑");
			return false;
		}

		// 优先取 body|Swim fast，没有就退化成任意 Swim 剪辑
		fastSwimClip = clips.FirstOrDefault(c => c.name.ToLower().Contains("fast"))
			?? clips.FirstOrDefault(c => c.name.ToLower().Contains("swim"))
			?? clips[0];
		idleClip = clips.FirstOrDefault(c => c.name.ToLower().Contains("idle"));

		if (idleClip == null)
			Debug.LogWarning("[沙丁鱼] 模型里没有 Idle 剪辑，抓起时将保持游泳动作");
		return true;
	}

	static AnimatorController BuildController(AnimationClip fastSwimClip, AnimationClip idleClip)
	{
		// 已经存在就原地改，不能删了重建：删掉会换 guid，预制体上的引用会断
		var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
		if (controller == null)
			controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);

		AnimatorStateMachine machine = controller.layers[0].stateMachine;

		// 清掉之前生成的状态，重新按 FastSwim / Idle 布局
		foreach (ChildAnimatorState child in machine.states.ToArray())
		{
			if (child.state != null) machine.RemoveState(child.state);
		}

		if (!controller.parameters.Any(p => p.name == "Held"))
			controller.AddParameter("Held", AnimatorControllerParameterType.Bool);

		AnimatorState fastSwimState = machine.AddState("FastSwim", new Vector3(300f, 120f, 0f));
		fastSwimState.motion = fastSwimClip;
		machine.defaultState = fastSwimState;

		if (idleClip != null)
		{
			AnimatorState idleState = machine.AddState("Idle", new Vector3(300f, 240f, 0f));
			idleState.motion = idleClip;

			// 抓在手里 → Idle；松开 → 回到 FastSwim
			AnimatorStateTransition toIdle = fastSwimState.AddTransition(idleState);
			toIdle.hasExitTime = false;
			toIdle.duration = 0.15f;
			toIdle.AddCondition(AnimatorConditionMode.If, 0f, "Held");

			AnimatorStateTransition toFastSwim = idleState.AddTransition(fastSwimState);
			toFastSwim.hasExitTime = false;
			toFastSwim.duration = 0.15f;
			toFastSwim.AddCondition(AnimatorConditionMode.IfNot, 0f, "Held");
		}

		EditorUtility.SetDirty(controller);
		return controller;
	}

	// 删掉旧的 Armature / SardineSkin，挂上新模型，并把根上缺的东西补齐
	static void SwapVisual(GameObject root, GameObject fbx, Avatar avatar, AnimatorController controller, out string detail)
	{
		// 旧的可视部分
		for (int i = root.transform.childCount - 1; i >= 0; i--)
			Object.DestroyImmediate(root.transform.GetChild(i).gameObject);

		// 根上的旧 Animator 指向旧骨骼，已失效；SardineCharacter 无人调用且会在 Start 里空引用
		var rootAnimator = root.GetComponent<Animator>();
		if (rootAnimator != null) Object.DestroyImmediate(rootAnimator);
		var character = root.GetComponent<JSukoAnimals.SardineCharacter>();
		if (character != null) Object.DestroyImmediate(character);

		// 新模型
		var model = (GameObject)Object.Instantiate(fbx, root.transform, false);
		model.name = "SardineModel";
		model.transform.localPosition = Vector3.zero;

		// 去掉 Blender 里带出来的两个棱角球
		var junk = new List<GameObject>();
		foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
		{
			bool isJunk = t.name.Contains("棱角球") || t.name.StartsWith("Icosphere");
			if (isJunk && t.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
				junk.Add(t.gameObject);
		}
		foreach (GameObject go in junk) Object.DestroyImmediate(go);

		// 缩放到旧鱼体长，并把模型中心对齐到根原点
		if (TryGetBounds(root.transform, model, out Bounds raw) && raw.size.magnitude > 0.0001f)
		{
			float rawLength = Mathf.Max(raw.size.x, Mathf.Max(raw.size.y, raw.size.z));
			model.transform.localScale *= TargetLength / rawLength;
			if (TryGetBounds(root.transform, model, out Bounds scaled))
			{
				// 只对齐水平方向：蒙皮网格的 localBounds 在竖直方向不可靠，
				// 按它居中会让鱼被抓起来时明显高于手，所以 Y 保持模型原点
				model.transform.localPosition -= new Vector3(scaled.center.x, 0f, scaled.center.z);
			}
		}

		// 材质：内嵌的 Standard 材质在 URP 下会变粉，转成 URP/Lit 并保留颜色
		foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
		{
			Material[] materials = renderer.sharedMaterials;
			for (int i = 0; i < materials.Length; i++)
				materials[i] = ToUrpLit(materials[i]);
			renderer.sharedMaterials = materials;
		}

		// 动画：Animator 放在模型根上，Avatar 的骨骼路径才对得上
		var animator = model.GetComponent<Animator>();
		if (animator == null) animator = model.AddComponent<Animator>();
		animator.avatar = avatar;
		animator.runtimeAnimatorController = controller;
		animator.applyRootMotion = false;
		animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

		// 碰撞体：旧的在被删掉的骨骼上，这里按新模型尺寸重建
		string colliderInfo = "沿用原有碰撞体";
		var capsule = root.GetComponent<CapsuleCollider>();
		if (root.GetComponent<Collider>() == null)
		{
			capsule = root.AddComponent<CapsuleCollider>();
			if (TryGetBounds(root.transform, model, out Bounds b))
			{
				Vector3 size = b.size;
				int axis = 0;
				if (size.y > size[axis]) axis = 1;
				if (size.z > size[axis]) axis = 2;

				capsule.direction = axis;
				capsule.center = b.center;
				capsule.radius = Mathf.Max(0.01f, Mathf.Min(size[(axis + 1) % 3], size[(axis + 2) % 3]) * 0.35f);
				capsule.height = Mathf.Max(size[axis] * 0.95f, capsule.radius * 2f);
				colliderInfo = $"新建 CapsuleCollider（半径 {capsule.radius:F3}，长度 {capsule.height:F3}）";
			}
		}

		detail = $"新模型 {model.name}，动画 {controller.name}，{colliderInfo}。"
			+ "如果鱼头朝向不对，选中预制体里的 SardineModel 调 Y 轴旋转即可。";
	}

	// 把渲染器（含蒙皮网格）的包围盒换算到 root 的本地空间
	static bool TryGetBounds(Transform space, GameObject modelRoot, out Bounds bounds)
	{
		bounds = new Bounds();
		bool has = false;

		foreach (Renderer renderer in modelRoot.GetComponentsInChildren<Renderer>(true))
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

			Matrix4x4 toSpace = space.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
			Vector3 center = toSpace.MultiplyPoint3x4(local.center);
			Vector3 extents = toSpace.MultiplyVector(local.extents);
			extents = new Vector3(Mathf.Abs(extents.x), Mathf.Abs(extents.y), Mathf.Abs(extents.z));

			var current = new Bounds(center, extents * 2f);
			if (!has)
			{
				bounds = current;
				has = true;
			}
			else
			{
				bounds.Encapsulate(current);
			}
		}

		return has;
	}

	// 内嵌 Standard 材质 → 新建 URP/Lit 材质资产，保留基础色
	static Material ToUrpLit(Material source)
	{
		if (source == null) return null;
		if (source.shader != null && source.shader.name.StartsWith("Universal Render Pipeline")) return source;

		EnsureFolder(MaterialDir);

		string name = string.IsNullOrEmpty(source.name) ? "SardineMat" : source.name;
		foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
			name = name.Replace(invalid, '_');

		string path = $"{MaterialDir}/{name}.mat";
		var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
		if (existing != null) return existing;

		Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
		if (urpLit == null)
		{
			Debug.LogWarning("[沙丁鱼] 工程里找不到 URP/Lit，材质保持原样");
			return source;
		}

		var material = new Material(urpLit) { name = name };
		Color color = source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
		material.SetColor("_BaseColor", color);
		if (source.HasProperty("_Metallic")) material.SetFloat("_Metallic", source.GetFloat("_Metallic"));
		if (source.HasProperty("_Glossiness")) material.SetFloat("_Smoothness", source.GetFloat("_Glossiness"));

		AssetDatabase.CreateAsset(material, path);
		return material;
	}

	static void EnsureFolder(string folder)
	{
		if (AssetDatabase.IsValidFolder(folder)) return;
		string parent = System.IO.Path.GetDirectoryName(folder)?.Replace('\\', '/');
		if (string.IsNullOrEmpty(parent)) return;
		EnsureFolder(parent);
		AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
	}
}
