#ifndef VR_DISSOLVE_COMMON_INCLUDED
#define VR_DISSOLVE_COMMON_INCLUDED

// 溶解材质各 Pass 共用的属性与噪波函数

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half _Metallic;
    half _Smoothness;
    half _DissolveAmount;
    half _EdgeWidth;
    half4 _EdgeColor;
    half _EdgeIntensity;
    float _NoiseScale;
CBUFFER_END

// 便宜的三维哈希噪波：不依赖贴图，模型空间采样，溶解图案固定不变
float DissolveHash(float3 p)
{
    p = frac(p * 0.3183099 + float3(0.1, 0.2, 0.3));
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float DissolveNoise(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);

    float n000 = DissolveHash(i + float3(0.0, 0.0, 0.0));
    float n100 = DissolveHash(i + float3(1.0, 0.0, 0.0));
    float n010 = DissolveHash(i + float3(0.0, 1.0, 0.0));
    float n110 = DissolveHash(i + float3(1.0, 1.0, 0.0));
    float n001 = DissolveHash(i + float3(0.0, 0.0, 1.0));
    float n101 = DissolveHash(i + float3(1.0, 0.0, 1.0));
    float n011 = DissolveHash(i + float3(0.0, 1.0, 1.0));
    float n111 = DissolveHash(i + float3(1.0, 1.0, 1.0));

    float nx00 = lerp(n000, n100, f.x);
    float nx10 = lerp(n010, n110, f.x);
    float nx01 = lerp(n001, n101, f.x);
    float nx11 = lerp(n011, n111, f.x);

    float nxy0 = lerp(nx00, nx10, f.y);
    float nxy1 = lerp(nx01, nx11, f.y);

    return lerp(nxy0, nxy1, f.z);
}

// 当前像素相对溶解阈值还剩多少（< 0 表示已经被挖掉）
float DissolveDelta(float3 positionOS)
{
    float noise = DissolveNoise(positionOS * max(0.001, _NoiseScale));
    return noise - _DissolveAmount * 1.05;
}

#endif
