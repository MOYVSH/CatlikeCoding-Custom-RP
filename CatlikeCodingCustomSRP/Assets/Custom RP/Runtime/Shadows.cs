using UnityEngine;
using UnityEngine.Rendering;

// �������Ϊ�ڹ��λ�÷�һ��������ܿ����ĵط���û��Ӱ���������ĵط�������Ӱ
// ���û���Ǿ�û����Ӱ
// �ӹ��չ����ķ������ǰ���ж�����ס�Ǿʹ�����Ӱ��
// ��Ӱ���㷨ʵ���Ͼ���ͨ�����ͼ������ڵ���ϵ




//����Shadow Map����߼������ϼ�ΪLighting��
public class Shadows
{
    private const string bufferName = "Shadows";

    private CommandBuffer buffer = new CommandBuffer()
    {
        name = bufferName
    };

    private ScriptableRenderContext context;

    private CullingResults cullingResults;

    private ShadowSettings settings;

    const int maxShadowedDirectionalLightCount = 4, maxCascades = 4;

    // ���ڻ�ȡ��ǰ֧����Ӱ�ķ����Դ��һЩ��Ϣ
    struct ShadowedDirectionalLight
    {        
        //��ǰ��Դ���������²������ΪCullingResults�й�Դ������(Ҳ��Lighting���µĹ�Դ���������Ƕ���ͳһ�ģ��ǳ�����~��
        public int visibleLightIndex;
        public float slopeScaleBias;
        public float nearPlaneOffset;
    }
    // ��Ȼ����Ŀǰ����Դ��Ϊ1������Ȼ������洢����Ϊ���������������~ 
    ShadowedDirectionalLight[] ShadowedDirectionalLights =
        new ShadowedDirectionalLight[maxShadowedDirectionalLightCount];
    // ��ǰ��������ϵķ����Դ��
    int ShadowedDirectionalLightCount;

    // �����ԴShadow Atlas����Ӱ�仯��������ı�ʶ
    static int dirShadowAtlasId = Shader.PropertyToID("_DirectionalShadowAtlas"),
               dirShadowMatricesId = Shader.PropertyToID("_DirectionalShadowMatrices"),
               cascadeCountId = Shader.PropertyToID("_CascadeCount"),
               cascadeCullingSpheresId = Shader.PropertyToID("_CascadeCullingSpheres"),
               cascadeDataId = Shader.PropertyToID("_CascadeData"),
               shadowAtlasSizeId = Shader.PropertyToID("_ShadowAtlasSize"),
               shadowDistanceFadeId = Shader.PropertyToID("_ShadowDistanceFade");

    static Vector4[] cascadeCullingSpheres = new Vector4[maxCascades],
                     cascadeData = new Vector4[maxCascades];
    // ����������ת������Ӱ��ͼ�ϵ���������ı任���� ����Ϊ�ƹ��� * ������Ӱ��
    static Matrix4x4[]
            dirShadowMatrices = new Matrix4x4[maxShadowedDirectionalLightCount * maxCascades];

    // pcf�Ĺؼ���
    static string[] directionalFilterKeywords = {
        "_DIRECTIONAL_PCF3",
        "_DIRECTIONAL_PCF5",
        "_DIRECTIONAL_PCF7",
    };

    // ������Ӱ��Ϲؼ���
    static string[] cascadeBlendKeywords = {
        "_CASCADE_BLEND_SOFT",
        "_CASCADE_BLEND_DITHER"
    };

    public void Setup(ScriptableRenderContext context, CullingResults cullingResults, ShadowSettings settings)
    {
        this.context = context;
        this.cullingResults = cullingResults;
        this.settings = settings;

        ShadowedDirectionalLightCount = 0;
    }

    // ÿִ֡�У�����Ϊlight����shadow altas��shadowMap����Ԥ��һƬ�ռ�����Ⱦ��Ӱ��ͼ��ͬʱ�洢һЩ������Ҫ��Ϣ
    public Vector3 ReserveDirectionalShadows(Light light, int visibleLightIndex) 
    {
        // ���ù�Դ�����������ֵ   
        // ֻ���ÿ�����Ӱ����Ӱǿ�ȴ���0�Ĺ�Դ
        // ���Բ���Ҫ��Ⱦ�κ���Ӱ�Ĺ�Դ��ͨ��cullingResults.GetShadowCasterBounds������
        if (ShadowedDirectionalLightCount < maxShadowedDirectionalLightCount && 
            light.shadows != LightShadows.None && 
            light.shadowStrength > 0f && 
            cullingResults.GetShadowCasterBounds(visibleLightIndex, out Bounds b))

        {
            ShadowedDirectionalLights[ShadowedDirectionalLightCount] =
                new ShadowedDirectionalLight
                {
                    visibleLightIndex = visibleLightIndex,
                    slopeScaleBias = light.shadowBias,
                    nearPlaneOffset = light.shadowNearPlane
                };
            return new Vector3(light.shadowStrength, 
                               settings.directional.cascadeCount * ShadowedDirectionalLightCount++, 
                               light.shadowNormalBias);
        }

        return Vector3.zero;
    }

    //��Ⱦ��Ӱ��ͼ
    public void Render()
    {
        if (ShadowedDirectionalLightCount > 0)
        {
            RenderDirectionalShadows();
        }
        else
        {
            //�����Ϊĳ��ԭ����Ҫ��Ⱦ��Ӱ������Ҳ��Ҫ����һ��1x1��С��ShadowAtlas
            //��ΪWebGL 2.0�����ĳ�����ʰ���ShadowMap���ڼ���ʱ��ʧ��ShadowMap�ᱨ��
            buffer.GetTemporaryRT(dirShadowAtlasId, 1, 1, 
                32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap);
        }
    }

    void RenderDirectionalShadows()
    {
        //Shadow Atlas��Ӱͼ���ĳߴ磬Ĭ��Ϊ1024
        int atlasSize = (int)settings.directional.atlasSize;
        //ʹ��CommandBuffer.GetTemporaryRT������һ��RT����Shadow Atlas��ע������ÿ֡�Լ��������ͷ�
        //��һ������Ϊ��RT�ı�ʶ���ڶ�������ΪRT�Ŀ�����������ΪRT�ĸ�
        //���ĸ�����ΪdepthBuffer��λ�����������Ϊ����ģʽ������������ΪRT��ʽ
        //����ʹ��32bits��Floatλ��URPʹ�õ���16bits
        buffer.GetTemporaryRT(dirShadowAtlasId, atlasSize, atlasSize,
            32, FilterMode.Bilinear, RenderTextureFormat.Shadowmap);
        //����GPU������������RT��ShadowAtlas
        //RenderBufferLoadAction.DontCare��ζ���ڽ�������ΪRenderTarget֮�����ǲ��������ĳ�ʼ״̬������������κ�Ԥ����
        //RenderBufferStoreAction.Store��ζ���������RT�ϵ�������Ⱦָ��֮��Ҫ�л�Ϊ��һ��RenderTargetʱ�������ǻὫ��洢���Դ���Ϊ��������ʹ��
        buffer.SetRenderTarget(dirShadowAtlasId, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
        //����ShadowAtlas��DepthBuffer�����ǵ�ShadowAtlasҲֻ��32bits��DepthBuffer��,��һ�β���true��ʾ���DepthBuffer���ڶ���false��ʾ�����ColorBuffer
        buffer.ClearRenderTarget(true, false, Color.clear);
        buffer.BeginSample(bufferName);
        ExecuteBuffer();

        int tiles = ShadowedDirectionalLightCount * settings.directional.cascadeCount;
        int split = tiles <= 1 ? 1 : tiles <= 4 ? 2 : 4;
        int tileSize = atlasSize / split;

        for (int i = 0; i < ShadowedDirectionalLightCount; i++)
        {
            RenderDirectionalShadows(i, split, tileSize);
        }
        buffer.SetGlobalInt(cascadeCountId, settings.directional.cascadeCount);
        buffer.SetGlobalVectorArray(cascadeCullingSpheresId, cascadeCullingSpheres);
        buffer.SetGlobalVectorArray(cascadeDataId, cascadeData);
        buffer.SetGlobalMatrixArray(dirShadowMatricesId, dirShadowMatrices);
        float f = 1f - settings.directional.cascadeFade;
        buffer.SetGlobalVector(
            shadowDistanceFadeId, new Vector4(
                1f / settings.maxDistance, 1f / settings.distanceFade,
                1f / (1f - f * f)
            )
        );
        SetKeywords(directionalFilterKeywords, (int)settings.directional.filter - 1);
        SetKeywords(cascadeBlendKeywords, (int)settings.directional.cascadeBlend - 1);
        buffer.SetGlobalVector(shadowAtlasSizeId, new Vector4(atlasSize, 1f / atlasSize));
        buffer.EndSample(bufferName);
        ExecuteBuffer();
    }

    void SetKeywords(string[] keywords, int enabledIndex)
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
    /// ��Ⱦ������Դ����Ӱ��ͼ��ShadowAtlas��
    /// </summary>
    /// <param name="index">��Դ������</param>
    /// <param name="split">�ֿ�����һ������</param>
    /// <param name="tileSize">�ù�Դ��ShadowAtlas�Ϸ����Tile���С</param>
    void RenderDirectionalShadows(int index, int split, int tileSize)
    {
        // ������Ҫ֪���������Դ��û��λ�����ݵģ�������Ϊ����Զ����
        // �������ͨ��VP������һ��������ü��ռ䣬������ҪͶ����Ӱ
        // ������ͨ����VP����任���òü��ռ��У�Ȼ����������Ϣ��Ⱦ
        // ��Ӱ��ͼ��

        //��ȡ��ǰҪ���ù�Դ����Ϣ
        ShadowedDirectionalLight light = ShadowedDirectionalLights[index];
        //����cullingResults�͵�ǰ��Դ������������һ��ShadowDrawingSettings
        var shadowSettings = new ShadowDrawingSettings(cullingResults, light.visibleLightIndex);

        //��ǰ���õ���Ӱ������
        int cascadeCount = settings.directional.cascadeCount;
        //��ǰҪ��Ⱦ�ĵ�һ��tile��ShadowAtlas�е�����
        int tileOffset = index * cascadeCount;
        //����Ratios��������Ⱦ����
        Vector3 ratios = settings.directional.CascadeRatios;

        float cullingFactor = Mathf.Max(0f, 0.8f - settings.directional.cascadeFade);

        for (int i = 0; i < cascadeCount; i++)
        {
            //ʹ��Unity�ṩ�Ľӿ���Ϊ�����Դ���������Ⱦ��Ӱ��ͼ�õ�VP�����splitData
            cullingResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(light.visibleLightIndex,
            i, cascadeCount, ratios,
            tileSize, light.nearPlaneOffset,
            out Matrix4x4 viewMatrix, out Matrix4x4 projectionMatrix, out ShadowSplitData splitData);
            // �޳�ƫ�� Culling Bias �޳���Χ����
            splitData.shadowCascadeBlendCullingFactor = cullingFactor;
            //splitData����Ͷ����Ӱ����Ӧ����α��ü�����Ϣ��������Ҫ�������ݸ�shadowSettings
            shadowSettings.splitData = splitData;

            if (index == 0)
            {
                SetCascadeData(i, splitData.cullingSphere, tileSize);
            }

            int tileIndex = tileOffset + i;
            //���õ�ǰҪ��Ⱦ��Tile����
            //������Ӱ�任����(����ռ䵽��Դ�ü��ռ䣩
            dirShadowMatrices[tileIndex] = ConvertToAtlasMatrix(projectionMatrix * viewMatrix,
                                                            SetTileViewport(tileIndex, split, tileSize),
                                                            split);
            //����ǰVP��������Ϊ�������VP����׼����Ⱦ��Ӱ��ͼ
            buffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);

            buffer.SetGlobalDepthBias(0f, light.slopeScaleBias);
            ExecuteBuffer();
            //ʹ��context.DrawShadows����Ⱦ��Ӱ��ͼ������Ҫ����һ��shadowSettings
            context.DrawShadows(ref shadowSettings);
            buffer.SetGlobalDepthBias(0f, 0f);
        }
    }

    void SetCascadeData(int index, Vector4 cullingSphere, float tileSize)
    {
        float texelSize = 2f * cullingSphere.w / tileSize;
        float filterSize = texelSize * ((float)settings.directional.filter + 1f);

        cullingSphere.w -= filterSize;
        cullingSphere.w *= cullingSphere.w;
        cascadeCullingSpheres[index] = cullingSphere;
        cascadeData[index] = new Vector4(1f / cullingSphere.w, filterSize * 1.4142136f);
    }

    /// <summary>
    /// ���õ�ǰҪ��Ⱦ��Tile����
    /// </summary>
    /// <param name="index">Tile����</param>
    /// <param name="split">Tileһ�������ϵ�����</param>
    /// <param name="tileSize">һ��Tile�Ŀ�ȣ��߶ȣ�</param>
    Vector2 SetTileViewport(int index, int split, float tileSize)
    {
        Vector2 offset = new Vector2(index % split, index / split);
        buffer.SetViewport(new Rect(
            offset.x * tileSize, offset.y * tileSize, tileSize, tileSize
        ));
        return offset;
    }

    Matrix4x4 ConvertToAtlasMatrix(Matrix4x4 m, Vector2 offset, int split)
    {
        //���ʹ�÷���Z��������ΪZȡ��
        if (SystemInfo.usesReversedZBuffer)
        {
            m.m20 = -m.m20;
            m.m21 = -m.m21;
            m.m22 = -m.m22;
            m.m23 = -m.m23;
        }
        //��Դ�ü��ռ����귶ΧΪ[-1,1]���������������ȶ���[0,1]����ˣ����ǽ��ü��ռ�����ת����[0,1]��
        //Ȼ��[0,1]�µ�x,yƫ�Ƶ���Դ��Ӧ��Tile��
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

    //�����ShadowAtlas���й������ͷ�ShadowAtlas RT
    public void Cleanup()
    {
        buffer.ReleaseTemporaryRT(dirShadowAtlasId);
        ExecuteBuffer();
    }

    void ExecuteBuffer()
    {
        context.ExecuteCommandBuffer(buffer);
        buffer.Clear();
    }
}