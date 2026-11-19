using UnityEngine;

namespace JSukoAnimals
{
	// 挂在鱼群父物体上：以该物体被放置的位置为圆心，并按 fishCount 自动补齐整群鱼
	[DisallowMultipleComponent]
	public class SardineGroupOrbit : MonoBehaviour
	{
		[Header("鱼群")]
		[Tooltip("整群鱼的数量（包含层级里那条模板鱼）")]
		[Min(1)] public int fishCount = 8;
		[Tooltip("绕圈半径（米）")]
		public float orbitRadius = 4.5f;
		[Tooltip("绕圈速度（度/秒）")]
		public float orbitSpeed = 60f;
		[Tooltip("鱼游动的最高速度（米/秒）")]
		public float swimSpeed = 8f;
		[Tooltip("圆心相对放置点的高度偏移（米）")]
		public float heightOffset = 0f;
		[Tooltip("上下起伏幅度（米），0 就是纯水平圆周")]
		public float verticalAmplitude = 1.2f;
		[Tooltip("上下起伏速度（度/秒）")]
		public float verticalSpeed = 30f;
		[Tooltip("鱼群球体半径（米），越大队伍越稀疏")]
		public float formationSpread = 2.2f;
		[Tooltip("是否允许整群鱼都能被抓；关闭后只有层级里那条模板鱼可抓（其余鱼会挡住射线）")]
		public bool grabMultipleFish = true;

		[Header("可见性剔除")]
		[Tooltip("整组看不见时暂停鱼的脚本、物理、动画与绘制")]
		public bool cullOffscreen = true;
		[Tooltip("超过这个距离或离开视锥就整组暂停（米）")]
		public float cullDistance = 20f;
		[Tooltip("多久检测一次可见性（秒）")]
		public float checkInterval = 0.2f;

		// 运行时
		Vector3 centerPosition;   // 鱼群被放置的位置，作为环绕圆心
		float angleDeg;
		float verticalDeg;
		bool ready;

		SardineUserController[] registeredFish;
		Camera viewCamera;
		float nextCheckTime;
		bool hidden;

		public Vector3 Center => centerPosition;
		public float AngleRadians => angleDeg * Mathf.Deg2Rad;
		public float VerticalRadians => verticalDeg * Mathf.Deg2Rad;

		void Awake()
		{
			Setup();
		}

		// 初始化：记录圆心 → 按数量补齐鱼 → 登记分组
		public void Setup()
		{
			if (ready) return;
			ready = true;

			centerPosition = transform.position;

			// 层级里的第一条鱼当作模板（方案 1：它本身也算一条）
			SardineUserController template = null;
			foreach (var fish in GetComponentsInChildren<SardineUserController>(true))
			{
				template = fish;
				break;
			}

			if (template == null)
			{
				Debug.LogWarning("SardineGroupOrbit：鱼群下面没有鱼模板，无法生成鱼群", this);
				return;
			}

			SpawnFish(template, fishCount);
			RegisterFish();
		}

		// 复制模板补齐数量；复制出的鱼不参与抓取，避免每条鱼都挂交互组件
		void SpawnFish(SardineUserController template, int targetCount)
		{
			int existing = GetComponentsInChildren<SardineUserController>(true).Length;
			int need = targetCount - existing;
			if (need <= 0) return;

			Quaternion rot = template.transform.rotation;

			for (int i = 0; i < need; i++)
			{
				// 在圆周上随机撒开，避免所有鱼叠在同一个点上
				float rad = Random.Range(0f, Mathf.PI * 2f);
				Vector3 pos = centerPosition + new Vector3(
					Mathf.Sin(rad) * orbitRadius,
					heightOffset + Random.Range(-verticalAmplitude, verticalAmplitude),
					Mathf.Cos(rad) * orbitRadius);

				SardineUserController clone =
					Instantiate(template, pos, rot, transform);
				if (clone == null) continue;

				// 模板如果被隐藏了，复制体要打开才看得见
				if (!clone.gameObject.activeSelf) clone.gameObject.SetActive(true);

				// 只保留模板那条可抓，其余的关掉抓取（注意：不可抓的鱼仍会挡住射线）
				if (!grabMultipleFish)
				{
					clone.grabbable = false;
					var grab = clone.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
					if (grab != null) grab.enabled = false;
				}
			}
		}

		// 把整组鱼登记到鸟群系统，并用斐波那契球面分配队形占位
		void RegisterFish()
		{
			SardineUserController[] fishes = GetComponentsInChildren<SardineUserController>(true);
			registeredFish = fishes;
			int total = Mathf.Max(1, fishes.Length);
			for (int i = 0; i < fishes.Length; i++)
			{
				fishes[i].AttachGroup(this, i, total);
			}
		}

		void FixedUpdate()
		{
			if (!ready) Setup();

			float dt = Time.fixedDeltaTime;
			angleDeg += orbitSpeed * dt;
			verticalDeg += verticalSpeed * dt;

			if (cullOffscreen) UpdateVisibility();
		}

		// 整组在视野外或太远时暂停，避免看不见的鱼还在消耗 CPU/GPU
		void UpdateVisibility()
		{
			if (registeredFish == null) return;
			if (Time.time < nextCheckTime) return;
			nextCheckTime = Time.time + Mathf.Max(0.05f, checkInterval);

			// 主相机就是 XR 头显，双目用同一个视锥判断即可
			if (viewCamera == null) viewCamera = Camera.main;
			if (viewCamera == null) return;

			Vector3 center = centerPosition + Vector3.up * heightOffset;
			float dist = Vector3.Distance(viewCamera.transform.position, center);

			// 整组的大致包围盒：绕圈半径 + 队形散布
			float extent = Mathf.Max(orbitRadius, 0.5f) + formationSpread;
			Bounds bounds = new Bounds(center, Vector3.one * (extent * 2f));
			Plane[] planes = GeometryUtility.CalculateFrustumPlanes(viewCamera);
			bool inFrustum = GeometryUtility.TestPlanesAABB(planes, bounds);

			bool visible = inFrustum && dist < cullDistance;
			if (visible == !hidden) return;

			hidden = !visible;
			for (int i = 0; i < registeredFish.Length; i++)
			{
				if (registeredFish[i] == null) continue;
				registeredFish[i].SetCulled(!visible);
			}
		}

		// 编辑器里画出行走圆周和上下起伏范围，方便直接调半径
		void OnDrawGizmos()
		{
			Vector3 center = transform.position + Vector3.up * heightOffset;

			Gizmos.color = Color.yellow;
			DrawCircle(center, orbitRadius, 40);

			if (verticalAmplitude > 0f)
			{
				Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.75f);
				DrawCircle(center + Vector3.up * verticalAmplitude, orbitRadius, 40);
				DrawCircle(center - Vector3.up * verticalAmplitude, orbitRadius, 40);
			}

			// 圆心到圆周的一条半径线
			Gizmos.color = Color.yellow;
			Gizmos.DrawLine(center, center + transform.right * orbitRadius);
		}

		static void DrawCircle(Vector3 center, float radius, int segments)
		{
			Vector3 prev = center + new Vector3(radius, 0f, 0f);
			for (int i = 1; i <= segments; i++)
			{
				float a = i * Mathf.PI * 2f / segments;
				Vector3 next = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
				Gizmos.DrawLine(prev, next);
				prev = next;
			}
		}
	}
}
