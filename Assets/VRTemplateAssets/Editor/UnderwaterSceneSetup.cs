using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace UnderwaterTools
{
	// 一次性配置工具：把当前场景做成水下氛围（雾 + 相机背景 + 后处理 Volume + 漂浮微粒/气泡）
	// 只在编辑器里用，配置完成后这个脚本可以删掉，生成的内容已经存进场景和工程资源
	public static class UnderwaterSceneSetup
	{
		const string ProfilePath = "Assets/VRTemplateAssets/Profiles/UnderwaterProfile.asset";
		const string TextureFolder = "Assets/VRTemplateAssets/Textures";
		const string MaterialFolder = "Assets/VRTemplateAssets/Materials";

		// 水下主色：偏青蓝，雾和相机背景必须一致，远处才能自然融进去
		static readonly Color FogColor = new Color(0.11f, 0.36f, 0.44f, 1f);

		[MenuItem("工具/水下氛围/一键配置场景")]
		static void Setup()
		{
			Camera cam = FindCamera();
			if (cam == null)
			{
				EditorUtility.DisplayDialog("水下氛围", "当前场景里没找到相机，请打开带 XR Origin 的场景后再执行。", "好");
				return;
			}

			ApplyFog();
			ApplyCamera(cam);

			VolumeProfile profile = EnsureProfile();
			EnsureGlobalVolume(profile);

			Texture2D dot = EnsureDotTexture();
			Material moteMaterial = EnsureMaterial(dot, MaterialFolder + "/UnderwaterMote.mat");
			Material bubbleMaterial = EnsureMaterial(dot, MaterialFolder + "/UnderwaterBubble.mat");

			EnsureMotes(cam, moteMaterial);
			EnsureBubbles(cam, bubbleMaterial);

			EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
			AssetDatabase.SaveAssets();
			Debug.Log("水下氛围配置完成：雾、相机背景、后处理 Volume、漂浮微粒、上浮气泡。记得 Ctrl+S 保存场景。");
		}

		static Camera FindCamera()
		{
			Camera cam = Camera.main;
			if (cam != null) return cam;

			Camera[] cameras = Object.FindObjectsOfType<Camera>(true);
			return cameras.Length > 0 ? cameras[0] : null;
		}

		// 方案 1：指数平方雾，密度 0.055，10 米约 26% 雾，20 米约 70% 雾
		static void ApplyFog()
		{
			RenderSettings.fog = true;
			RenderSettings.fogMode = FogMode.ExponentialSquared;
			RenderSettings.fogColor = FogColor;
			RenderSettings.fogDensity = 0.055f;
		}

		// 方案 1：相机背景改成和雾同色的纯色，不要天空盒
		static void ApplyCamera(Camera cam)
		{
			cam.clearFlags = CameraClearFlags.SolidColor;
			cam.backgroundColor = FogColor;

			// 方案 2 依赖相机开启后处理，否则 Volume 不生效
			UniversalAdditionalCameraData data = cam.GetComponent<UniversalAdditionalCameraData>();
			if (data == null) data = cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
			data.renderPostProcessing = true;

			EditorUtility.SetDirty(cam);
			EditorUtility.SetDirty(data);
		}

		// 方案 2：调色 + Bloom + Vignette
		static VolumeProfile EnsureProfile()
		{
			VolumeProfile profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
			if (profile == null)
			{
				if (!AssetDatabase.IsValidFolder("Assets/VRTemplateAssets/Profiles"))
				{
					AssetDatabase.CreateFolder("Assets/VRTemplateAssets", "Profiles");
				}

				profile = ScriptableObject.CreateInstance<VolumeProfile>();
				AssetDatabase.CreateAsset(profile, ProfilePath);
			}

			if (!profile.TryGet(out ColorAdjustments color))
			{
				color = profile.Add<ColorAdjustments>(true);
				AssetDatabase.AddObjectToAsset(color, profile);
			}
			color.colorFilter.Override(new Color(0.80f, 0.93f, 1f));
			color.saturation.Override(-12f);
			color.contrast.Override(6f);
			color.postExposure.Override(0f);

			if (!profile.TryGet(out Bloom bloom))
			{
				bloom = profile.Add<Bloom>(true);
				AssetDatabase.AddObjectToAsset(bloom, profile);
			}
			bloom.intensity.Override(0.35f);
			bloom.threshold.Override(1.1f);
			bloom.scatter.Override(0.65f);
			bloom.highQualityFiltering.Override(false);

			if (!profile.TryGet(out Vignette vignette))
			{
				vignette = profile.Add<Vignette>(true);
				AssetDatabase.AddObjectToAsset(vignette, profile);
			}
			vignette.intensity.Override(0.28f);
			vignette.smoothness.Override(0.45f);
			vignette.color.Override(new Color(0.02f, 0.06f, 0.09f, 1f));

			EditorUtility.SetDirty(profile);
			AssetDatabase.SaveAssets();
			return profile;
		}

		static void EnsureGlobalVolume(VolumeProfile profile)
		{
			GameObject go = GameObject.Find("Global Volume");
			if (go == null) go = new GameObject("Global Volume");

			Volume volume = go.GetComponent<Volume>();
			if (volume == null) volume = go.AddComponent<Volume>();
			volume.isGlobal = true;
			volume.priority = 1f;
			volume.sharedProfile = profile;

			EditorUtility.SetDirty(go);
		}

		// 方案 3：跟随相机的微尘，世界空间模拟，玩家移动时不会粘在脸上
		static void EnsureMotes(Camera cam, Material material)
		{
			if (cam.transform.Find("UnderwaterMotes") != null) return;

			GameObject go = new GameObject("UnderwaterMotes");
			go.transform.SetParent(cam.transform, false);
			go.transform.localPosition = Vector3.zero;
			go.transform.localRotation = Quaternion.identity;

			ParticleSystem ps = go.AddComponent<ParticleSystem>();

			var main = ps.main;
			main.loop = true;
			main.prewarm = true;
			main.duration = 10f;
			main.startLifetime = new ParticleSystem.MinMaxCurve(8f, 14f);
			main.startSpeed = new ParticleSystem.MinMaxCurve(0.02f, 0.08f);
			main.startSize = new ParticleSystem.MinMaxCurve(0.006f, 0.018f);
			main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.75f, 0.95f, 1f, 0.35f));
			main.maxParticles = 500;
			main.simulationSpace = ParticleSystemSimulationSpace.World;

			var emission = ps.emission;
			emission.rateOverTime = 45f;

			var shape = ps.shape;
			shape.shapeType = ParticleSystemShapeType.Box;
			shape.scale = new Vector3(8f, 5f, 10f);
			shape.position = new Vector3(0f, 0f, 6f);

			var velocity = ps.velocityOverLifetime;
			velocity.enabled = true;
			velocity.space = ParticleSystemSimulationSpace.World;
			velocity.x = new ParticleSystem.MinMaxCurve(-0.03f, 0.03f);
			velocity.y = new ParticleSystem.MinMaxCurve(0f, 0.03f);
			velocity.z = new ParticleSystem.MinMaxCurve(-0.03f, 0.03f);

			ParticleSystemRenderer psRenderer = go.GetComponent<ParticleSystemRenderer>();
			psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
			psRenderer.sharedMaterial = material;
			psRenderer.maxParticleSize = 0.05f;
			psRenderer.shadowCastingMode = ShadowCastingMode.Off;
			psRenderer.receiveShadows = false;

			ps.Play();
		}

		// 方案 3：稀疏的上浮气泡串
		static void EnsureBubbles(Camera cam, Material material)
		{
			if (cam.transform.Find("UnderwaterBubbles") != null) return;

			GameObject go = new GameObject("UnderwaterBubbles");
			go.transform.SetParent(cam.transform, false);
			go.transform.localPosition = Vector3.zero;
			go.transform.localRotation = Quaternion.identity;

			ParticleSystem ps = go.AddComponent<ParticleSystem>();

			var main = ps.main;
			main.loop = true;
			main.prewarm = true;
			main.duration = 10f;
			main.startLifetime = new ParticleSystem.MinMaxCurve(5f, 9f);
			main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.15f);
			main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
			main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.80f, 0.95f, 1f, 0.55f));
			main.maxParticles = 80;
			main.simulationSpace = ParticleSystemSimulationSpace.World;

			var emission = ps.emission;
			emission.rateOverTime = 2.5f;

			var shape = ps.shape;
			shape.shapeType = ParticleSystemShapeType.Sphere;
			shape.radius = 2.5f;
			shape.position = new Vector3(0f, 0f, 4f);

			var velocity = ps.velocityOverLifetime;
			velocity.enabled = true;
			velocity.space = ParticleSystemSimulationSpace.World;
			velocity.x = new ParticleSystem.MinMaxCurve(-0.05f, 0.05f);
			velocity.y = new ParticleSystem.MinMaxCurve(0.20f, 0.45f);
			velocity.z = new ParticleSystem.MinMaxCurve(-0.05f, 0.05f);

			ParticleSystemRenderer psRenderer = go.GetComponent<ParticleSystemRenderer>();
			psRenderer.renderMode = ParticleSystemRenderMode.Billboard;
			psRenderer.sharedMaterial = material;
			psRenderer.maxParticleSize = 0.1f;
			psRenderer.shadowCastingMode = ShadowCastingMode.Off;
			psRenderer.receiveShadows = false;

			ps.Play();
		}

		// 生成一张柔边圆点贴图，避免粒子显示成方块
		static Texture2D EnsureDotTexture()
		{
			string assetPath = TextureFolder + "/UnderwaterDot.png";
			Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
			if (existing != null) return existing;

			if (!AssetDatabase.IsValidFolder(TextureFolder))
			{
				AssetDatabase.CreateFolder("Assets/VRTemplateAssets", "Textures");
			}

			const int size = 64;
			Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
			Color32[] pixels = new Color32[size * size];
			float center = (size - 1) * 0.5f;

			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center)) / center;
					float alpha = Mathf.Clamp01(1f - dist);
					alpha = alpha * alpha * (3f - 2f * alpha);   // 平滑过渡，边缘更软
					pixels[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
				}
			}

			tex.SetPixels32(pixels);
			tex.Apply();

			string projectRoot = Directory.GetParent(Application.dataPath).FullName;
			File.WriteAllBytes(Path.Combine(projectRoot, assetPath), tex.EncodeToPNG());
			Object.DestroyImmediate(tex);
			AssetDatabase.ImportAsset(assetPath);

			TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
			if (importer != null)
			{
				importer.alphaIsTransparency = true;
				importer.mipmapEnabled = false;
				importer.wrapMode = TextureWrapMode.Clamp;
				importer.filterMode = FilterMode.Bilinear;
				importer.SaveAndReimport();
			}

			return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
		}

		// 半透明粒子材质：优先 URP 粒子 Unlit，找不到就退回 Sprites/Default
		static Material EnsureMaterial(Texture2D texture, string assetPath)
		{
			Material existing = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
			if (existing != null) return existing;

			if (!AssetDatabase.IsValidFolder(MaterialFolder))
			{
				AssetDatabase.CreateFolder("Assets/VRTemplateAssets", "Materials");
			}

			Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
			if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
			if (shader == null) shader = Shader.Find("Sprites/Default");

			Material material = new Material(shader);
			material.SetTexture("_BaseMap", texture);
			material.SetTexture("_MainTex", texture);
			material.SetColor("_BaseColor", Color.white);

			if (shader.name.StartsWith("Universal Render Pipeline"))
			{
				material.SetFloat("_Surface", 1f);   // Transparent
				material.SetFloat("_Blend", 0f);     // Alpha
				material.SetFloat("_ZWrite", 0f);
				material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
				material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
				material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
				material.SetOverrideTag("RenderType", "Transparent");
				material.renderQueue = (int)RenderQueue.Transparent;
			}

			AssetDatabase.CreateAsset(material, assetPath);
			return material;
		}
	}
}
