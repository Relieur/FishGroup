using UnityEngine;

namespace VRTest.Environment
{
	// 让气泡发射器始终跟着玩家（只跟水平位置）：
	// 粒子本身是世界空间的，不会粘在玩家身上；发射区随玩家移动，走到哪都有气泡
	[AddComponentMenu("VR/Environment/Bubble Field")]
	public class BubbleField : MonoBehaviour
	{
		[Tooltip("跟随目标；留空则自动使用 Main Camera")]
		public Transform target;

		[Tooltip("水平跟随的平滑速度；0 = 直接跟随（推荐，避免气泡被拖着走）")]
		public float followSharpness = 0f;

		[Tooltip("跟随点在 XZ 上相对玩家的偏移（米）")]
		public Vector3 horizontalOffset = Vector3.zero;

		void LateUpdate()
		{
			if (target == null)
			{
				Camera cam = Camera.main;
				if (cam == null) cam = FindFirstObjectByType<Camera>();
				if (cam == null) return;
				target = cam.transform;
			}

			Vector3 want = new Vector3(
				target.position.x + horizontalOffset.x,
				transform.position.y,
				target.position.z + horizontalOffset.z);

			transform.position = followSharpness <= 0f
				? want
				: Vector3.Lerp(transform.position, want, 1f - Mathf.Exp(-followSharpness * Time.deltaTime));
		}
	}
}
