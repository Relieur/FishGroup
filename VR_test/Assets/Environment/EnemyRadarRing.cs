using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace VRTest.Environment
{
	// 方位 HUD：固定在视野里的椭圆周界上，每只怪对应一段两端渐细的弧形
	// 用 UGUI Image + 程序生成的弧形贴图，弧长按米固定，不会抖动
	[AddComponentMenu("VR/Environment/Enemy Radar HUD")]
	public class EnemyRadarRing : MonoBehaviour
	{
		[Header("跟随相机")]
		[Tooltip("跟随目标；留空自动用 Main Camera")]
		public Transform target;

		[Tooltip("画布离相机的距离（米）")]
		public float distanceFromCamera = 0.65f;

		[Header("椭圆周界（米，相对相机）")]
		[Tooltip("椭圆的水平半轴（米）")]
		public float radiusX = 0.45f;

		[Tooltip("椭圆的垂直半轴（米）")]
		public float radiusY = 0.34f;

		[Header("弧段（米）")]
		[Tooltip("每段弧的固定长度（米）")]
		public float arcLength = 0.16f;

		[Tooltip("弧最粗处（中段）的厚度（米），两端会收细到尖")]
		public float arcThickness = 0.030f;

		[Tooltip("弧向内收缩的距离（米），保证整条弧不被画面边缘裁掉")]
		public float edgePadding = 0.045f;

		[Header("颜色")]
		public Color arcColor = new Color(1f, 0.62f, 0.66f, 0.55f);

		[Tooltip("死亡状态下是否隐藏")]
		public bool hideWhenEnemyDead = true;

		[Header("采样")]
		public float scanInterval = 0.35f;

		// 画布像素与米的换算基准
		const float PixelsPerMeter = 1000f;

		// 纹理里：带厚（最粗处）与弯曲幅度各占纹理高度的比例
		const float BandThicknessFraction = 0.36f;
		const float CurveFraction = 0.35f;

		// 运行时
		Transform m_Follow;
		Canvas m_Canvas;
		RectTransform m_ArcRoot;

		readonly List<LuffyEnemy> m_Enemies = new List<LuffyEnemy>();
		readonly List<Image> m_ArcPool = new List<Image>();
		float m_NextScan;

		void Awake()
		{
			ResolveFollow();
			AttachToFollow();
			BuildUI();
		}

		void BuildUI()
		{
			var canvasGo = new GameObject("RadarCanvas", typeof(RectTransform), typeof(Canvas));
			canvasGo.transform.SetParent(transform, false);

			m_Canvas = canvasGo.GetComponent<Canvas>();
			m_Canvas.renderMode = RenderMode.WorldSpace;
			m_Canvas.sortingOrder = 15;

			// 1000px = 1m
			var canvasRect = canvasGo.GetComponent<RectTransform>();
			canvasRect.sizeDelta = new Vector2(PixelsPerMeter * 2f, PixelsPerMeter * 2f);
			canvasRect.localScale = new Vector3(1f / PixelsPerMeter, 1f / PixelsPerMeter, 1f);

			var rootGo = new GameObject("Arcs", typeof(RectTransform));
			rootGo.transform.SetParent(canvasGo.transform, false);
			m_ArcRoot = rootGo.GetComponent<RectTransform>();
			m_ArcRoot.anchorMin = m_ArcRoot.anchorMax = new Vector2(0.5f, 0.5f);
			m_ArcRoot.pivot = new Vector2(0.5f, 0.5f);
			m_ArcRoot.anchoredPosition = Vector2.zero;
			m_ArcRoot.sizeDelta = Vector2.zero;
		}

		void LateUpdate()
		{
			if (m_Follow == null) ResolveFollow();
			if (m_Follow == null) return;

			AttachToFollow();
			RescanEnemies();
			LayoutArcs();
		}

		// 直接挂在相机下面，不再用插值追位置：
		// 平滑跟随会让画布比视线慢半拍，转头时弧段看起来在抖
		void AttachToFollow()
		{
			if (transform.parent != m_Follow) transform.SetParent(m_Follow, false);

			transform.localPosition = Vector3.forward * distanceFromCamera;
			transform.localRotation = Quaternion.identity;
			transform.localScale = Vector3.one;
		}

		void ResolveFollow()
		{
			if (m_Follow != null) return;
			if (target != null) { m_Follow = target; return; }

			Camera cam = Camera.main;
			if (cam == null) cam = FindFirstObjectByType<Camera>();
			if (cam != null) m_Follow = cam.transform;
		}

		void RescanEnemies()
		{
			if (Time.time < m_NextScan) return;
			m_NextScan = Time.time + Mathf.Max(0.05f, scanInterval);

			m_Enemies.Clear();
			foreach (var e in FindObjectsOfType<LuffyEnemy>())
			{
				if (e == null) continue;
				if (hideWhenEnemyDead && !e.IsAlive) continue;
				m_Enemies.Add(e);
			}
		}

		void LayoutArcs()
		{
			Camera cam = m_Follow.GetComponent<Camera>();
			Transform camT = cam != null ? cam.transform : m_Follow;

			Vector3 fwd = camT.forward;
			fwd.y = 0f;
			float camYaw = fwd.sqrMagnitude > 0.0001f
				? Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg
				: 0f;

			float rxPx = radiusX * PixelsPerMeter;
			float ryPx = radiusY * PixelsPerMeter;
			float widthPx = arcLength * PixelsPerMeter;

			// 纹理高 = 弧厚 / 带厚占比，弯曲幅度按同一比例换算
			float heightPx = arcThickness / BandThicknessFraction * PixelsPerMeter;

			// 内缩后的安全半径
			float safeX = Mathf.Max(1f, rxPx - edgePadding * PixelsPerMeter);
			float safeY = Mathf.Max(1f, ryPx - edgePadding * PixelsPerMeter);

			for (int i = 0; i < m_Enemies.Count; i++)
			{
				var enemy = m_Enemies[i];
				Image img = GetOrCreateArc(i);
				if (img == null) continue;

				if (enemy == null) { img.enabled = false; continue; }

				Vector3 to = enemy.transform.position - camT.position;
				Vector3 flat = new Vector3(to.x, 0f, to.z);
				if (flat.sqrMagnitude < 0.0001f) { img.enabled = false; continue; }

				float worldAng = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
				float relDeg = Mathf.DeltaAngle(0f, worldAng - camYaw);

				img.enabled = true;

				float rad = relDeg * Mathf.Deg2Rad;
				float px = Mathf.Sin(rad) * safeX;
				float py = Mathf.Cos(rad) * safeY;

				RectTransform rt = img.rectTransform;
				rt.anchoredPosition = new Vector2(px, py);

				// 沿椭圆切线摆放：点 (rx*sin, ry*cos) 求导得切线 (rx*cos, -ry*sin)
				float tx = safeX * Mathf.Cos(rad);
				float ty = -safeY * Mathf.Sin(rad);
				float tangentDeg = Mathf.Atan2(ty, tx) * Mathf.Rad2Deg;
				rt.localRotation = Quaternion.Euler(0f, 0f, tangentDeg);

				// 固定尺寸，绝不随距离变化
				rt.sizeDelta = new Vector2(widthPx, heightPx);
			}

			for (int i = m_Enemies.Count; i < m_ArcPool.Count; i++)
			{
				if (m_ArcPool[i] != null) m_ArcPool[i].enabled = false;
			}
		}

		Image GetOrCreateArc(int index)
		{
			while (m_ArcPool.Count <= index)
			{
				var go = new GameObject("Arc" + m_ArcPool.Count,
					typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
				go.transform.SetParent(m_ArcRoot, false);

				var img = go.GetComponent<Image>();
				img.color = arcColor;
				img.raycastTarget = false;
				img.sprite = GetArcSprite();
				img.type = Image.Type.Simple;

				var rt = img.rectTransform;
				rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
				rt.pivot = new Vector2(0.5f, 0.5f);

				m_ArcPool.Add(img);
			}

			return m_ArcPool[index];
		}

		// ---- 程序生成弧形贴图：中段粗、两端收成尖，并且整体带弧度 ----
		static Sprite s_ArcSprite;

		static Sprite GetArcSprite()
		{
			if (s_ArcSprite != null) return s_ArcSprite;

			const int W = 256, H = 96;

			float maxHalf = H * BandThicknessFraction * 0.5f;   // 最粗处的半厚
			float curvePx = H * CurveFraction;                  // 中段相对两端的抬升

			var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
			tex.wrapMode = TextureWrapMode.Clamp;
			tex.filterMode = FilterMode.Bilinear;

			var px = new Color32[W * H];
			for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 0);

			for (int x = 0; x < W; x++)
			{
				float t = x / (float)(W - 1);        // 0..1
				float tt = 2f * t - 1f;              // -1..1

				// 中心线：中段抬起，形成弧
				float centerY = H * 0.5f + curvePx * ((1f - tt * tt) - 0.5f);

				// 半厚：中间最粗，两端收细
				float taper = Mathf.Pow(Mathf.Sin(Mathf.PI * t), 0.62f);
				float half = maxHalf * taper;

				int y0 = Mathf.Max(0, Mathf.FloorToInt(centerY - half - 1f));
				int y1 = Mathf.Min(H - 1, Mathf.CeilToInt(centerY + half + 1f));

				for (int y = y0; y <= y1; y++)
				{
					// 1px 抗锯齿
					float d = Mathf.Abs(y - centerY);
					float a = Mathf.Clamp01(half - d + 0.5f);
					if (a <= 0f) continue;

					int idx = y * W + x;
					px[idx] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
				}
			}

			tex.SetPixels32(px);
			tex.Apply();

			s_ArcSprite = Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f),
				100f, 0, SpriteMeshType.FullRect);
			s_ArcSprite.name = "RadarTaperedArc";
			return s_ArcSprite;
		}

		void OnDestroy()
		{
			if (m_ArcRoot != null) Destroy(m_ArcRoot.gameObject);
			if (m_Canvas != null) Destroy(m_Canvas.gameObject);
		}
	}
}