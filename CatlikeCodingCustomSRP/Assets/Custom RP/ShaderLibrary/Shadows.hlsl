#ifndef CUSTOM_SHADOWS_INCLUDED
#define CUSTOM_SHADOWS_INCLUDED

#define MAX_SHADOWED_DIRECTIONAL_LIGHT_COUNT 4
#define MAX_CASCADE_COUNT 4

TEXTURE2D_SHADOW(_DirectionalShadowAtlas);
#define SHADOW_SAMPLER sampler_linear_clamp_compare
SAMPLER_CMP(SHADOW_SAMPLER);

CBUFFER_START(_CustomShadows)
int _CascadeCount;
float4 _CascadeCullingSpheres[MAX_CASCADE_COUNT];
float4x4 _DirectionalShadowMatrices[MAX_SHADOWED_DIRECTIONAL_LIGHT_COUNT * MAX_CASCADE_COUNT];
float4 _ShadowDistanceFade;
CBUFFER_END

struct DirectionalShadowData 
{
	float strength;
	int tileIndex;
};

struct ShadowData
{
    int cascadeIndex;
    float strength;
};

/**
 * \brief 计算考虑渐变的级联阴影强度
 * \param distance 当前片元深度
 * \param scale 1/maxDistance 将当前片元深度缩放到 [0,1]内
 * \param fade 渐变比例，值越大，开始衰减的距离越远，衰减速度越大
 * \return 级联阴影强度
 */
float FadedShadowStrength(float distance, float scale, float fade)
{
    return saturate((1.0 - distance * scale) * fade);
}

//计算给定片元将要使用的级联信息
ShadowData GetShadowData(Surface surfaceWS)
{
    ShadowData data;
    data.strength = FadedShadowStrength(surfaceWS.depth, _ShadowDistanceFade.x, _ShadowDistanceFade.y);
        int i;
    for (i = 0; i < _CascadeCount; i++)
    {
        float4 sphere = _CascadeCullingSpheres[i];
        float distanceSqr = DistanceSquared(surfaceWS.position, sphere.xyz);
        if (distanceSqr < sphere.w)
        {
            if (i == _CascadeCount - 1)
            {
                data.strength *= FadedShadowStrength(
					distanceSqr, 1.0 / sphere.w, _ShadowDistanceFade.z
				);
            }
            break;
        }
    }
    
    if (i == _CascadeCount)
    {
        data.strength = 0.0;
    }
    
    data.cascadeIndex = i;

    return data;
}

float SampleDirectionalShadowAtlas(float3 positionSTS)
{
	// 通过 阴影采样器和阴影纹理空间中的位置 对阴影图集进行采样
    return SAMPLE_TEXTURE2D_SHADOW(
		_DirectionalShadowAtlas, SHADOW_SAMPLER, positionSTS
	);
}

float GetDirectionalShadowAttenuation(DirectionalShadowData data, Surface surfaceWS)
{
    if (data.strength <= 0.0)
    {
        return 1.0;
    }
	
    float3 positionSTS = mul(
		_DirectionalShadowMatrices[data.tileIndex],
		float4(surfaceWS.position, 1.0)
	).xyz;
    float shadow = SampleDirectionalShadowAtlas(positionSTS);
	
    return lerp(1.0, shadow, data.strength);
}


#endif