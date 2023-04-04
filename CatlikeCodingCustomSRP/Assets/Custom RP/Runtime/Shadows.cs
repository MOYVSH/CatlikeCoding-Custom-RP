using UnityEngine;
using UnityEngine.Rendering;

//所有Shadow Map相关逻辑，其上级为Lighting类
public class Shadows
{
    private const string bufferName = "Shadows";
    //支持阴影的方向光源最大数（注意这里，我们可以有多个方向光源，但支持的阴影的最多只有4个）
    private const int maxShadowedDirectionalLightCount = 4, maxCascades = 4;
    //软阴影的PCF过滤模式Shader关键字
    private static string[] directionalFilterKeywords =
    {
        "_DIRECTIONAL_PCF3",
        "_DIRECTIONAL_PCF5",
        "_DIRECTIONAL_PCF7"
    };
    //软阴影、抖动级联混合关键字
    private static string[] cascadeBlendKeywords =
    {
        "_CASCADE_BLEND_SOFT",
        "_CASCADE_BLEND_DITHER"
    };

    // 阴影遮罩关键字
    static string[] shadowMaskKeywords = {
        "_SHADOW_MASK_ALWAYS",
        "_SHADOW_MASK_DISTANCE"
    };

    //方向光源Shadow Atlas、阴影变化矩阵数组的标识、级联总数、单个级联的CullingSphere索引、级联信息、PCF过滤需要的阴影贴图信息（atlas大小、texel大小）、Vector3(最大阴影距离，渐变距离比例，最大级联渐变比例）
    private static int dirShadowAtlasId = Shader.PropertyToID("_DirectionalShadowAtlas"),
        dirShadowMatricesId = Shader.PropertyToID("_DirectionalShadowMatrices"),
        cascadeCountId = Shader.PropertyToID("_CascadeCount"),
        cascadeCullingSpheresId = Shader.PropertyToID("_CascadeCullingSpheres"),
        cascadeDataId = Shader.PropertyToID("_CascadeData"),
        shadowAtlasSizeId = Shader.PropertyToID("_ShadowAtlasSize"),
        shadowDistanceFadeId = Shader.PropertyToID("_ShadowDistanceFade");
    //将世界坐标转换到阴影贴图上的像素坐标的变换矩阵
    private static Matrix4x4[] dirShadowMatrices = new Matrix4x4[maxShadowedDirectionalLightCount * maxCascades];
    //每个级联的Culling Shpere信息（xyz为球心坐标，w为半径）、级联信息
    private static Vector4[] cascadeCullingShperes = new Vector4[maxCascades],
        cascadeData = new Vector4[maxCascades];
    
    private CommandBuffer buffer = new CommandBuffer()
    {
        name = bufferName
    };

    private ScriptableRenderContext context;

    private CullingResults cullingResults;

    private ShadowSettings settings;

    private bool useShadowMask;

    //用于获取当前支持阴影的方向光源的一些信息
    struct ShadowedDirectionalLight
    {
        //当前光源的索引，猜测该索引为CullingResults中光源的索引(也是Lighting类下的光源索引，它们都是统一的，非常不错~）
        public int visibleLightIndex;
        //当前光源的slopeScaleBias
        public float slopeScaleBias;
        //光源阴影裁剪视锥体近平面偏移（向后）
        public float nearPlaneOffset;
    }

    //虽然我们目前最大光源数为1，但依然用数组存储，因为最大数量可配置嘛~
    private ShadowedDirectionalLight[] ShadowedDirectionalLights =
        new ShadowedDirectionalLight[maxShadowedDirectionalLightCount];

    //当前已配置完毕的方向光源数
    private int ShadowedDirectionalLightCount;

    public void Setup(ScriptableRenderContext context, CullingResults cullingResults,
        ShadowSettings settings)
    {
        this.context = context;
        this.cullingResults = cullingResults;
        this.settings = settings;
        //每帧初始时ShadowedDirectionalLightCount为0，在配置每个光源时其+1
        ShadowedDirectionalLightCount = 0;
        useShadowMask = false;
    }

    void ExecuteBuffer()
    {
        context.ExecuteCommandBuffer(buffer);
        buffer.Clear();
    }

    //每帧执行，用于为light配置shadow altas（shadowMap）上预留一片空间来渲染阴影贴图，同时存储一些其他必要信息
    //返回每个光源的阴影强度、其第一个级联的索引、光源的阴影法线偏移，传递给GPU存储到Light结构体
    public Vector4 ReserveDirectionalShadows(Light light, int visibleLightIndex)
    {
        //配置光源数不超过最大值
        //只配置开启阴影且阴影强度大于0的光源
        //忽略不需要渲染任何阴影的光源（通过cullingResults.GetShadowCasterBounds方法）
        if (ShadowedDirectionalLightCount < maxShadowedDirectionalLightCount && 
            light.shadows != LightShadows.None && 
            light.shadowStrength > 0f)
        {
            float maskChannel = -1;
            LightBakingOutput lightBaking = light.bakingOutput;
            if (
                lightBaking.lightmapBakeType == LightmapBakeType.Mixed &&
                lightBaking.mixedLightingMode == MixedLightingMode.Shadowmask
            )
            {
                useShadowMask = true;
                maskChannel = lightBaking.occlusionMaskChannel;
            }

            if (!cullingResults.GetShadowCasterBounds(visibleLightIndex, out Bounds b))
            {
                return new Vector4(-light.shadowStrength, 0f, 0f, maskChannel);
            }

            ShadowedDirectionalLights[ShadowedDirectionalLightCount] = new ShadowedDirectionalLight()
            {
                visibleLightIndex = visibleLightIndex,
                //slopeScaleBias直接读取原生light组件上的shadowBias属性
                slopeScaleBias = light.shadowBias,
                nearPlaneOffset = light.shadowNearPlane
            };
            return new Vector4(light.shadowStrength,
                               settings.directional.cascadeCount * ShadowedDirectionalLightCount++, 
                               light.shadowNormalBias, 
                               maskChannel);
        }
        return new Vector4(0f, 0f, 0f, -1f);
    }

    //渲染阴影贴图
    public void Render()
    {
        if (ShadowedDirectionalLightCount > 0)
        {
            RenderDirectionalShadows();
        }
        else
        {
            //如果因为某种原因不需要渲染阴影，我们也需要生成一张1x1大小的ShadowAtlas
            //因为WebGL 2.0下如果某个材质包含ShadowMap但在加载时丢失了ShadowMap会报错
            buffer.GetTemporaryRT(dirShadowAtlasId, 1, 1, 32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap);
        }
        buffer.BeginSample(bufferName);
        SetKeywords(shadowMaskKeywords, useShadowMask ?
            QualitySettings.shadowmaskMode == ShadowmaskMode.Shadowmask ? 0 : 1 :
            -1
        );
        buffer.EndSample(bufferName);
        ExecuteBuffer();
    }

    //渲染方向光源的Shadow Map到ShadowAtlas上
    void RenderDirectionalShadows()
    {
        //Shadow Atlas阴影图集的尺寸，默认为1024
        int atlasSize = (int)settings.directional.atlasSize;
        //使用CommandBuffer.GetTemporaryRT来申请一张RT用于Shadow Atlas，注意我们每帧自己管理其释放
        //第一个参数为该RT的标识，第二个参数为RT的宽，第三个参数为RT的高
        //第四个参数为depthBuffer的位宽，第五个参数为过滤模式，第六个参数为RT格式
        //我们使用32bits的Float位宽，URP使用的是16bits
        buffer.GetTemporaryRT(dirShadowAtlasId, atlasSize, atlasSize,
            32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap);
        //告诉GPU接下来操作的RT是ShadowAtlas
        //RenderBufferLoadAction.DontCare意味着在将其设置为RenderTarget之后，我们不关心它的初始状态，不对其进行任何预处理
        //RenderBufferStoreAction.Store意味着完成这张RT上的所有渲染指令之后（要切换为下一个RenderTarget时），我们会将其存储到显存中为后续采样使用
        buffer.SetRenderTarget(dirShadowAtlasId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
        //清理ShadowAtlas的DepthBuffer（我们的ShadowAtlas也只有32bits的DepthBuffer）,第一次参数true表示清除DepthBuffer，第二个false表示不清除ColorBuffer
        buffer.ClearRenderTarget(true, false, Color.clear);
        buffer.BeginSample(bufferName);
        ExecuteBuffer();

        //给ShadowAtlas分Tile，每个级联图一个Tile
        int tiles = ShadowedDirectionalLightCount * settings.directional.cascadeCount;
        int split = tiles <= 1 ? 1 : tiles <= 4 ? 2 : 4;
        int tileSize = atlasSize / split;

        //为每个配置好的方向光源配置其ShadowAtlas上的Tile
        for (int i = 0; i < ShadowedDirectionalLightCount; i++)
        {
            RenderDirectionalShadows(i, split, tileSize);
        }
        //传递所有阴影变换矩阵给GPU
        buffer.SetGlobalMatrixArray(dirShadowMatricesId, dirShadowMatrices);
        //传递Vecotr3(1/maxShadowDistance,1/distanceFade, 1-(1-f)^2)给GPU，在cpu中处理成倒数，gpu中只需要做一次乘法（效率比除法高）
        float f = 1f - settings.directional.cascadeFade;
        buffer.SetGlobalVector(shadowDistanceFadeId, new Vector4(1f/settings.maxDistance,1/settings.distanceFade,1f / (1f - f*f)));
        //设置PCF关键字
        SetKeywords(directionalFilterKeywords, (int)settings.directional.filter - 1);
        SetKeywords(cascadeBlendKeywords, (int)settings.directional.cascadeBlendMode - 1);
        //传递Shadow Atlas的尺寸和Texel大小
        buffer.SetGlobalVector(shadowAtlasSizeId, new Vector4(atlasSize, 1f / atlasSize));
        buffer.EndSample(bufferName);
        ExecuteBuffer();
    }

    /// <summary>
    /// 渲染单个光源的阴影贴图到ShadowAtlas上
    /// </summary>
    /// <param name="index">光源的索引</param>
    /// /// <param name="split">分块量（一个方向）</param>
    /// <param name="tileSize">该光源在ShadowAtlas上分配的Tile块大小</param>
    void RenderDirectionalShadows(int index, int split, int tileSize)
    {
        //获取当前要配置光源的信息
        ShadowedDirectionalLight light = ShadowedDirectionalLights[index];
        //根据cullingResults和当前光源的索引来构造一个ShadowDrawingSettings
        var shadowSettings = new ShadowDrawingSettings(cullingResults, light.visibleLightIndex);
        //当前配置的阴影级联数
        int cascadeCount = settings.directional.cascadeCount;
        //当前要渲染的第一个tile在ShadowAtlas中的索引
        int tileOffset = index * cascadeCount;
        //级联Ratios（控制渲染区域）
        Vector3 ratios = settings.directional.CascadeRatios;
        //定义级联剔除ShadowCaster的范围，值越小，剔除的对象越少，级联共享的渲染对象越多
        float cullingFactor = Mathf.Max(0f, 0.8f - settings.directional.cascadeFade);
        //渲染每个级联的阴影贴图
        for (int i = 0; i < cascadeCount; i++)
        {
            //使用Unity提供的接口来为方向光源计算出其渲染阴影贴图用的VP矩阵和splitData
            cullingResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(light.visibleLightIndex,
                i, cascadeCount, ratios,
                tileSize, light.nearPlaneOffset,
                out Matrix4x4 viewMatrix, out Matrix4x4 projectionMatrix, out ShadowSplitData splitData);
            //对于一个级联，剔除其已经出现在上个级联被渲染掉的ShadowCaster，减少重复渲染
            splitData.shadowCascadeBlendCullingFactor = cullingFactor;
            //splitData包括投射阴影物体应该如何被裁剪的信息，我们需要把它传递给shadowSettings
            shadowSettings.splitData = splitData;
            //只需要设置一次每个级联的Culling Spheres信息，因为其坐标为光源空间下，相对每个光源位置都一样
            if (index == 0)
            {
                SetCascadeData(i, splitData.cullingSphere, tileSize);
            }
            int tileIndex = tileOffset + i;
            //设置当前要渲染的Tile区域
            //设置阴影变换矩阵(世界空间到光源裁剪空间）
            dirShadowMatrices[tileIndex] =
                ConvertToAtlasMatrix(projectionMatrix * viewMatrix, SetTileViewport(tileIndex, split, tileSize), split);
            //将级联信息传递给GPU
            buffer.SetGlobalInt(cascadeCountId, settings.directional.cascadeCount);
            buffer.SetGlobalVectorArray(cascadeCullingSpheresId, cascadeCullingShperes);
            buffer.SetGlobalVectorArray(cascadeDataId, cascadeData);
            //将当前VP矩阵设置为计算出的VP矩阵，准备渲染阴影贴图
            buffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
            //在渲染阴影贴图前设置depth Bias来消除阴影痤疮,传入bias和slopBias
            //这里的bias单位应该不是米
            buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);
            ExecuteBuffer();
            //使用context.DrawShadows来渲染阴影贴图，其需要传入一个shadowSettings
            context.DrawShadows(ref shadowSettings);
            //渲染完阴影贴图后将bias设置回0
            buffer.SetGlobalDepthBias(0f, 0f);
        }
    }

    /// <summary>
    /// 设置Shader关键字
    /// </summary>
    void SetKeywords(string[] keywords,int enabledIndex)
    {
        for (int i = 0; i < keywords.Length; i++)
        {
            if (i == enabledIndex)
            {
                buffer.EnableShaderKeyword(keywords[i]);
            }
            else
            {
                buffer.DisableShaderKeyword(keywords[i]);
            }
        }
    }

    /// <summary>
    /// 设置当前要渲染的Tile区域
    /// </summary>
    /// <param name="index">Tile索引</param>
    /// <param name="split">Tile一个方向上的总数</param>
    /// <param name="tileSize">一个Tile的宽度（高度）</param>
    Vector2 SetTileViewport(int index, int split, float tileSize)
    {
        Vector2 offset = new Vector2(index % split, index / split);
        buffer.SetViewport(new Rect(offset.x * tileSize, offset.y * tileSize,tileSize,tileSize));
        return offset;
    }

    Matrix4x4 ConvertToAtlasMatrix(Matrix4x4 m, Vector2 offset, int split)
    {
        //如果使用反向Z缓冲区，为Z取反
        if (SystemInfo.usesReversedZBuffer)
        {
            m.m20 = -m.m20;
            m.m21 = -m.m21;
            m.m22 = -m.m22;
            m.m23 = -m.m23;
        }
        //光源裁剪空间坐标范围为[-1,1]，而纹理坐标和深度都是[0,1]，因此，我们将裁剪空间坐标转化到[0,1]内
        //然后将[0,1]下的x,y偏移到光源对应的Tile上
        float scale = 1f / split;
        m.m00 = (0.5f * (m.m00 + m.m30) + offset.x * m.m30) * scale;
        m.m01 = (0.5f * (m.m01 + m.m31) + offset.x * m.m31) * scale;
        m.m02 = (0.5f * (m.m02 + m.m32) + offset.x * m.m32) * scale;
        m.m03 = (0.5f * (m.m03 + m.m33) + offset.x * m.m33) * scale;
        m.m10 = (0.5f * (m.m10 + m.m30) + offset.y * m.m30) * scale;
        m.m11 = (0.5f * (m.m11 + m.m31) + offset.y * m.m31) * scale;
        m.m12 = (0.5f * (m.m12 + m.m32) + offset.y * m.m32) * scale;
        m.m13 = (0.5f * (m.m13 + m.m33) + offset.y * m.m33) * scale;
        m.m20 = 0.5f * (m.m20 + m.m30);
        m.m21 = 0.5f * (m.m21 + m.m31);
        m.m22 = 0.5f * (m.m22 + m.m32);
        m.m23 = 0.5f * (m.m23 + m.m33);
        return m;
    }

    /// <summary>
    /// 初始设置级联数据，后续会将这些数据传递给GPU
    /// </summary>
    /// <param name="index">级联索引</param>
    /// <param name="cullingSphere">级联CullingSphere</param>
    /// <param name="tileSize">tile大小</param>
    void SetCascadeData(int index, Vector4 cullingSphere, float tileSize)
    {
        //根据CullingSphere的半径大致推算出当前级联的纹素大小
        float texelSize = 2f * cullingSphere.w / tileSize;
        float filterSize = texelSize * ((float)settings.directional.filter + 1f);
        //cascadeData[i]：级联球半径倒数、大致纹素大小用于Normal Bias、
        cascadeData[index] = new Vector4(1f / cullingSphere.w, filterSize * 1.4142136f);
        //在cpu端对小球半径平方，方便在shader中计算片元与Culling Sphere的距离
        //防止PCF采样时越界
        cullingSphere.w -= filterSize;
        cullingSphere.w *= cullingSphere.w;
        cascadeCullingShperes[index] = cullingSphere;
    }

    //完成因ShadowAtlas所有工作后，释放ShadowAtlas RT
    public void Cleanup()
    {
        buffer.ReleaseTemporaryRT(dirShadowAtlasId);
        ExecuteBuffer();
    }
}
