using System.Collections.Generic;
using UnityEngine;

// 挂在 Trees 父物体上：把它的每个子物体当作一种树的模板，
// 在指定区域里均匀随机散布，并按类型补齐数量，避免互相重叠
[DisallowMultipleComponent]
public class TreeScatterer : MonoBehaviour
{
	[Header("散布区域")]
	[Tooltip("勾选时用 Trees 物体自身的位置作为区域中心")]
	public bool useOwnPositionAsCenter = true;
	[Tooltip("未勾选时使用这个世界坐标作为区域中心")]
	public Vector3 areaCenter = Vector3.zero;
	[Tooltip("区域大小：X 为宽度，Y 为长度（米）")]
	public Vector2 areaSize = new Vector2(40f, 40f);

	[Header("数量与密度")]
	[Tooltip("每种树生成多少棵（包含场景里已有的那一棵）")]
	[Min(1)] public int countPerType = 3;
	[Tooltip("两棵树之间的最小间距（米），越大越稀疏、越小越密集")]
	[Min(0f)] public float minSpacing = 4f;
	[Tooltip("找位置时的最大尝试次数，次数越多越不容易漏生成")]
	[Min(1)] public int placementAttempts = 30;

	[Header("外观随机")]
	[Tooltip("绕 Y 轴随机朝向；勾选了“正面朝相机”时会被覆盖")]
	public bool randomRotation = true;
	[Tooltip("随机缩放范围（统一缩放的倍数）")]
	public Vector2 scaleRange = new Vector2(0.85f, 1.15f);

	[Header("朝向")]
	[Tooltip("单面素材勾选后始终绕 Y 轴正面朝向玩家（Plants 建议开，Trees 关）")]
	public bool faceCamera = false;
	[Tooltip("朝向修正角度（度）；素材背面朝相机时填 180")]
	public float faceCameraYawOffset = 0f;

	[Header("贴合地面")]
	[Tooltip("向下打射线找到地面高度；关闭则沿用模板自身的高度")]
	public bool snapToGround = true;
	[Tooltip("地面所在的层")]
	public LayerMask groundMask = ~0;
	[Tooltip("贴地后的高度微调（米）")]
	public float groundOffset = 0f;
	[Tooltip("射线起点在区域上方多高开始往下打（米）")]
	public float rayStartHeight = 20f;

	[Header("随机种子")]
	[Tooltip("0 表示每次运行都随机；填非 0 可以复现同一批位置")]
	public int randomSeed = 0;

	[Header("避让装饰物")]
	[Tooltip("物体名字（含父级）包含这些关键字时，不在它附近生成树；多个用逗号分隔，不区分大小写")]
	public string avoidNameKeywords = "coral,seaweed,kelp,珊瑚,水草";
	[Tooltip("避让范围在物体包围盒之外再多留的距离（米）")]
	public float avoidPadding = 1.5f;
	[Tooltip("额外指定要避让的物体（其下所有渲染器的包围盒都算，可留空）")]
	public Transform[] avoidObjects;

	// 俯视投影上的一个圆形避让区域
	struct AvoidCircle
	{
		public Vector2 center;
		public float radius;
	}

	readonly List<AvoidCircle> avoidCircles = new List<AvoidCircle>();

	void Awake()
	{
		Scatter();
	}

	// 以子树为模板：每棵已有树先随机挪位，再按数量补足同类
	void Scatter()
	{
		if (transform.childCount == 0)
		{
			Debug.LogWarning("TreeScatterer：这个散布组下面没有任何模板", this);
			return;
		}

		// 先把子物体收集起来，避免生成过程中遍历到新加的树
		Transform[] templates = new Transform[transform.childCount];
		for (int i = 0; i < templates.Length; i++)
			templates[i] = transform.GetChild(i);

		System.Random rng = new System.Random(randomSeed != 0 ? randomSeed : System.Environment.TickCount);

		// 先把场景里手动摆的珊瑚、水草等装饰收集成避让区域
		CollectAvoidCircles();

		Vector3 center = useOwnPositionAsCenter ? transform.position : areaCenter;
		List<Vector3> placed = new List<Vector3>();

		for (int t = 0; t < templates.Length; t++)
		{
			Transform template = templates[t];
			if (template == null) continue;

			// 先记下模板原始姿态：第 0 棵会直接挪动模板本身，后面的复制不能受影响
			Vector3 baseScale = template.localScale;
			Quaternion baseRotation = template.rotation;
			float baseY = template.position.y;

			for (int i = 0; i < countPerType; i++)
			{
				Vector3 position = FindPosition(rng, center, baseY, placed);
				placed.Add(position);

				Quaternion rotation = randomRotation
					? Quaternion.Euler(0f, NextFloat(rng, 0f, 360f), 0f)
					: baseRotation;
				Vector3 scale = baseScale * NextFloat(rng, scaleRange.x, scaleRange.y);

				if (i == 0)
					ApplyToExisting(template, position, rotation, scale);
				else
					SpawnCopy(template, position, rotation, scale);
			}
		}
	}

	// 场景里已有的那棵直接改坐标，不额外生成
	void ApplyToExisting(Transform template, Vector3 position, Quaternion rotation, Vector3 scale)
	{
		template.SetPositionAndRotation(position, rotation);
		template.localScale = scale;
		template.gameObject.SetActive(true);
		EnsureBillboard(template);
	}

	Transform SpawnCopy(Transform template, Vector3 position, Quaternion rotation, Vector3 scale)
	{
		Transform copy = Instantiate(template, position, rotation, transform);
		copy.gameObject.name = template.name;
		copy.localScale = scale;
		copy.gameObject.SetActive(true);
		EnsureBillboard(copy);
		return copy;
	}

	// 单面素材需要每帧朝向相机，给模板和复制体都挂上
	void EnsureBillboard(Transform target)
	{
		if (!faceCamera) return;

		BillboardY billboard = target.GetComponent<BillboardY>();
		if (billboard == null)
			billboard = target.gameObject.AddComponent<BillboardY>();
		billboard.yawOffset = faceCameraYawOffset;
	}

	// 区域内均匀随机取点，并满足最小间距；尝试次数用完则返回最后一个点
	Vector3 FindPosition(System.Random rng, Vector3 center, float fallbackY, List<Vector3> placed)
	{
		for (int i = 0; i < placementAttempts; i++)
		{
			Vector3 candidate = RandomPoint(rng, center, fallbackY);
			if (CanPlace(candidate, placed))
			{
				candidate.y = ResolveGroundHeight(candidate, fallbackY);
				return candidate;
			}
		}

		// 兜底：间距实在排不开时，至少别落进珊瑚、水草里
		Vector3 fallback = RandomPoint(rng, center, fallbackY);
		for (int i = 0; i < placementAttempts * 4 && InsideAvoidArea(fallback); i++)
			fallback = RandomPoint(rng, center, fallbackY);

		fallback.y = ResolveGroundHeight(fallback, fallbackY);
		return fallback;
	}

	Vector3 RandomPoint(System.Random rng, Vector3 center, float fallbackY)
	{
		return new Vector3(
			center.x + NextFloat(rng, -areaSize.x * 0.5f, areaSize.x * 0.5f),
			fallbackY,
			center.z + NextFloat(rng, -areaSize.y * 0.5f, areaSize.y * 0.5f));
	}

	bool CanPlace(Vector3 candidate, List<Vector3> placed)
	{
		if (InsideAvoidArea(candidate)) return false;

		if (placed == null) return true;

		float minSqr = minSpacing * minSpacing;
		for (int i = 0; i < placed.Count; i++)
		{
			Vector3 offset = placed[i] - candidate;
			offset.y = 0f;
			if (offset.sqrMagnitude < minSqr)
				return false;
		}

		return true;
	}

	// 候选点是否落在珊瑚、水草等装饰的避让范围内
	bool InsideAvoidArea(Vector3 candidate)
	{
		for (int i = 0; i < avoidCircles.Count; i++)
		{
			AvoidCircle circle = avoidCircles[i];
			float dx = circle.center.x - candidate.x;
			float dz = circle.center.y - candidate.z;
			if (dx * dx + dz * dz < circle.radius * circle.radius)
				return true;
		}

		return false;
	}

	// 按关键字扫场景里手动摆放的装饰，用它们的包围盒生成避让圆
	void CollectAvoidCircles()
	{
		avoidCircles.Clear();

		string[] keywords = ParseKeywords(avoidNameKeywords);
		if (keywords.Length > 0)
		{
			Renderer[] renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
			for (int i = 0; i < renderers.Length; i++)
			{
				Renderer r = renderers[i];
				if (r == null) continue;
				if (r.transform.IsChildOf(transform)) continue;   // 自己这个散布组不算
				if (!NameMatches(r.transform, keywords)) continue;

				AddAvoidCircle(r.bounds);
			}
		}

		// 手动指定的物体：整棵子树的包围盒都算
		if (avoidObjects != null)
		{
			for (int i = 0; i < avoidObjects.Length; i++)
			{
				Transform target = avoidObjects[i];
				if (target == null) continue;

				Renderer[] renderers = target.GetComponentsInChildren<Renderer>();
				for (int j = 0; j < renderers.Length; j++)
					AddAvoidCircle(renderers[j].bounds);
			}
		}
	}

	void AddAvoidCircle(Bounds bounds)
	{
		avoidCircles.Add(new AvoidCircle
		{
			center = new Vector2(bounds.center.x, bounds.center.z),
			// 用包围盒水平方向的对角半径，保证任何朝向的模型都被完整覆盖
			radius = Mathf.Sqrt(bounds.extents.x * bounds.extents.x + bounds.extents.z * bounds.extents.z) + avoidPadding
		});
	}

	static string[] ParseKeywords(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return new string[0];

		string[] parts = raw.Split(new[] { ',', '，', ';', '；' }, System.StringSplitOptions.RemoveEmptyEntries);
		List<string> keywords = new List<string>();
		for (int i = 0; i < parts.Length; i++)
		{
			string keyword = parts[i].Trim().ToLowerInvariant();
			if (keyword.Length > 0) keywords.Add(keyword);
		}

		return keywords.ToArray();
	}

	// 物体自身或任意父级名字命中关键字就算
	static bool NameMatches(Transform target, string[] keywords)
	{
		Transform current = target;
		for (int depth = 0; current != null && depth < 8; depth++, current = current.parent)
		{
			string name = current.name.ToLowerInvariant();
			for (int i = 0; i < keywords.Length; i++)
			{
				if (name.Contains(keywords[i])) return true;
			}
		}

		return false;
	}

	// 取地面上最远（最低）的一个命中点，避免打到树自己的碰撞体
	float ResolveGroundHeight(Vector3 position, float fallbackY)
	{
		if (!snapToGround)
			return fallbackY;

		float startY = Mathf.Max(position.y, transform.position.y) + rayStartHeight;
		Vector3 origin = new Vector3(position.x, startY, position.z);
		float maxDistance = rayStartHeight + 100f;

		RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, maxDistance, groundMask, QueryTriggerInteraction.Ignore);
		float bestDistance = -1f;
		float groundY = fallbackY;

		for (int i = 0; i < hits.Length; i++)
		{
			// 跳过自己生成的树，别把树冠当成地面
			if (hits[i].transform.IsChildOf(transform)) continue;
			if (hits[i].distance > bestDistance)
			{
				bestDistance = hits[i].distance;
				groundY = hits[i].point.y;
			}
		}

		return groundY + groundOffset;
	}

	static float NextFloat(System.Random rng, float min, float max)
	{
		return min + (float)rng.NextDouble() * (max - min);
	}

	// 在编辑器里画出散布区域，方便对着地图调大小
	void OnDrawGizmosSelected()
	{
		Vector3 center = useOwnPositionAsCenter ? transform.position : areaCenter;
		Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.9f);

		Vector3 cornerA = center + new Vector3(-areaSize.x * 0.5f, 0f, -areaSize.y * 0.5f);
		Vector3 cornerB = center + new Vector3(areaSize.x * 0.5f, 0f, -areaSize.y * 0.5f);
		Vector3 cornerC = center + new Vector3(areaSize.x * 0.5f, 0f, areaSize.y * 0.5f);
		Vector3 cornerD = center + new Vector3(-areaSize.x * 0.5f, 0f, areaSize.y * 0.5f);

		Gizmos.DrawLine(cornerA, cornerB);
		Gizmos.DrawLine(cornerB, cornerC);
		Gizmos.DrawLine(cornerC, cornerD);
		Gizmos.DrawLine(cornerD, cornerA);
	}
}
