using System.Collections;
using UnityEngine;

// 怪物死亡溶解：死亡瞬间把模型渲染器换成可溶解的材质副本，
// 在 duration 内把 _DissolveAmount 从 0 拉到 1，播完还原原材质（复活后还能正常显示）
[AddComponentMenu("VR/Dissolve Effect")]
[DisallowMultipleComponent]
public class DissolveEffect : MonoBehaviour
{
	[Header("溶解")]
	[Tooltip("溶解持续时间（秒）")]
	[Min(0.05f)] public float duration = 1.4f;
	[Tooltip("溶解边缘的颜色")]
	public Color edgeColor = new Color(1f, 0.62f, 0.15f, 1f);
	[Tooltip("边缘亮带宽度，越大烧灼边越宽越明显")]
	[Min(0.001f)] public float edgeWidth = 0.16f;
	[Tooltip("边缘亮度")]
	public float edgeIntensity = 6f;
	[Tooltip("模型上大约分成多少块噪波，越大碎块越细")]
	[Min(0.5f)] public float noiseScale = 4f;
	[Tooltip("溶解时模型缩小到的比例，1 表示不缩小")]
	[Range(0.2f, 1f)] public float shrinkTo = 0.85f;

	const string k_DissolveShaderName = "VR/Dissolve Lit";

	static readonly int k_DissolveAmount = Shader.PropertyToID("_DissolveAmount");
	static readonly int k_EdgeColor = Shader.PropertyToID("_EdgeColor");
	static readonly int k_EdgeWidth = Shader.PropertyToID("_EdgeWidth");
	static readonly int k_EdgeIntensity = Shader.PropertyToID("_EdgeIntensity");
	static readonly int k_NoiseScale = Shader.PropertyToID("_NoiseScale");
	static readonly int k_BaseMap = Shader.PropertyToID("_BaseMap");
	static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");

	Renderer[] m_Renderers;
	Material[][] m_OriginalMaterials;
	Material[][] m_DissolveMaterials;
	Shader m_DissolveShader;
	bool m_Playing;

	public bool IsPlaying => m_Playing;

	// 协程播放：yield return effect.Play()，播完自动还原材质
	public IEnumerator Play()
	{
		if (m_Playing) yield break;

		m_DissolveShader = Shader.Find(k_DissolveShaderName);
		if (m_DissolveShader == null)
		{
			Debug.LogWarning("[Dissolve] 找不到 Shader：" + k_DissolveShaderName, this);
			yield break;
		}

		m_Playing = true;
		BuildMaterials();

		float total = Mathf.Max(0.05f, duration);
		Vector3 baseScale = transform.localScale;
		float elapsed = 0f;
		while (elapsed < total)
		{
			elapsed += Time.deltaTime;
			float t = Mathf.Clamp01(elapsed / total);

			SetAmount(t);

			// 一边溶解一边缩一点，远看也能看出是「化掉」而不是直接消失
			transform.localScale = Vector3.Lerp(baseScale, baseScale * shrinkTo, t);
			yield return null;
		}
		SetAmount(1f);
		transform.localScale = baseScale;

		RestoreMaterials();
		m_Playing = false;
	}

	void OnDestroy()
	{
		DestroyMaterials();
	}

	// 给每个渲染器换上溶解材质副本，不动原始共享材质
	void BuildMaterials()
	{
		m_Renderers = GetComponentsInChildren<Renderer>(true);
		m_OriginalMaterials = new Material[m_Renderers.Length][];
		m_DissolveMaterials = new Material[m_Renderers.Length][];

		for (int i = 0; i < m_Renderers.Length; i++)
		{
			Renderer renderer = m_Renderers[i];
			if (renderer == null) continue;

			Material[] originals = renderer.sharedMaterials;
			m_OriginalMaterials[i] = originals;

			// 按模型的真实尺寸换算噪波密度，避免模型大小不同导致「整只一起消失」
			Vector3 size = renderer.bounds.size;
			float maxSize = Mathf.Max(0.001f, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
			Vector3 scale = renderer.transform.lossyScale;
			float modelScale = Mathf.Max(0.0001f, Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))));
			float density = Mathf.Clamp(noiseScale * modelScale / maxSize, 0.01f, 400f);

			Material[] copies = new Material[originals.Length];
			for (int j = 0; j < originals.Length; j++)
			{
				copies[j] = CreateDissolveMaterial(originals[j], density);
			}

			m_DissolveMaterials[i] = copies;
			renderer.sharedMaterials = copies;
		}
	}

	Material CreateDissolveMaterial(Material source, float density)
	{
		Material material = new Material(m_DissolveShader);
		material.name = source != null ? source.name + "_Dissolve" : "Dissolve";

		if (source != null)
		{
			// URP Lit 的属性名能直接对上；其它 Shader 再按常见的贴图/颜色名兜底
			if (source.HasProperty("_BaseMap") && source.GetTexture("_BaseMap") != null)
			{
				material.SetTexture(k_BaseMap, source.GetTexture("_BaseMap"));
				material.SetTextureScale(k_BaseMap, source.GetTextureScale("_BaseMap"));
				material.SetTextureOffset(k_BaseMap, source.GetTextureOffset("_BaseMap"));
			}
			else
			{
				CopyFirstTexture(source, material);
			}

			if (source.HasProperty("_BaseColor")) material.SetColor(k_BaseColor, source.GetColor("_BaseColor"));
			else if (source.HasProperty("_Color")) material.SetColor(k_BaseColor, source.GetColor("_Color"));

			if (source.HasProperty("_Metallic")) material.SetFloat("_Metallic", source.GetFloat("_Metallic"));
			if (source.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", source.GetFloat("_Smoothness"));
			else if (source.HasProperty("_Glossiness")) material.SetFloat("_Smoothness", source.GetFloat("_Glossiness"));
		}

		material.SetFloat(k_DissolveAmount, 0f);
		material.SetColor(k_EdgeColor, edgeColor);
		material.SetFloat(k_EdgeWidth, edgeWidth);
		material.SetFloat(k_EdgeIntensity, edgeIntensity);
		material.SetFloat(k_NoiseScale, density);
		return material;
	}

	static void CopyFirstTexture(Material source, Material target)
	{
		string[] names = { "_MainTex", "_BaseColorMap", "_Albedo", "_Texture2D_1", "_Texture2D" };
		for (int i = 0; i < names.Length; i++)
		{
			if (!source.HasProperty(names[i])) continue;

			Texture texture = source.GetTexture(names[i]);
			if (texture == null) continue;

			target.SetTexture(k_BaseMap, texture);
			target.SetTextureScale(k_BaseMap, source.GetTextureScale(names[i]));
			target.SetTextureOffset(k_BaseMap, source.GetTextureOffset(names[i]));
			return;
		}
	}

	void SetAmount(float amount)
	{
		if (m_DissolveMaterials == null) return;

		for (int i = 0; i < m_DissolveMaterials.Length; i++)
		{
			Material[] materials = m_DissolveMaterials[i];
			if (materials == null) continue;

			for (int j = 0; j < materials.Length; j++)
			{
				if (materials[j] != null) materials[j].SetFloat(k_DissolveAmount, amount);
			}
		}
	}

	void RestoreMaterials()
	{
		if (m_Renderers != null)
		{
			for (int i = 0; i < m_Renderers.Length; i++)
			{
				if (m_Renderers[i] != null && m_OriginalMaterials != null && m_OriginalMaterials[i] != null)
					m_Renderers[i].sharedMaterials = m_OriginalMaterials[i];
			}
		}

		DestroyMaterials();
		m_Renderers = null;
		m_OriginalMaterials = null;
	}

	void DestroyMaterials()
	{
		if (m_DissolveMaterials == null) return;

		for (int i = 0; i < m_DissolveMaterials.Length; i++)
		{
			Material[] materials = m_DissolveMaterials[i];
			if (materials == null) continue;

			for (int j = 0; j < materials.Length; j++)
			{
				if (materials[j] != null) Destroy(materials[j]);
			}
		}

		m_DissolveMaterials = null;
	}
}
