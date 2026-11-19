using UnityEngine;

namespace VRTest.Environment
{
	// 水面波纹动画：让材质的 UV 持续滚动，并从水下抬头时保持可见
	[AddComponentMenu("VR/Environment/Water Surface")]
	public class WaterSurface : MonoBehaviour
	{
		[Header("波纹滚动")]
		[Tooltip("主波纹的滚动速度（UV/秒），建议 0.02~0.06")]
		public Vector2 scrollSpeedA = new Vector2(0.03f, 0.021f);

		[Tooltip("次波纹的滚动速度，与主波纹方向不同才能形成交织感")]
		public Vector2 scrollSpeedB = new Vector2(-0.017f, 0.033f);

		[Header("颜色")]
		public Color waterColor = new Color(0.55f, 0.85f, 1f, 0.42f);

		Material mat;
		Vector2 offsetA;
		Vector2 offsetB;

		void Start()
		{
			var renderer = GetComponent<Renderer>();
			if (renderer == null) return;

			// 用实例材质，避免改到共享资产
			mat = renderer.material;
			if (mat != null && mat.HasProperty("_BaseColor"))
				mat.SetColor("_BaseColor", waterColor);
		}

		void Update()
		{
			if (mat == null) return;

			// 单张图用交错的双向滚动做近似：主偏移持续增大，
			// 叠加反向偏移在视觉上形成两组不同速度的波纹
			offsetA += scrollSpeedA * Time.deltaTime;
			offsetB += scrollSpeedB * Time.deltaTime;

			// 两个偏移各取一半，合成一个最终的 UV 偏移
			Vector2 combined = offsetA * 0.5f + offsetB * 0.5f;
			mat.SetTextureOffset("_BaseMap", combined);
		}
	}
}
