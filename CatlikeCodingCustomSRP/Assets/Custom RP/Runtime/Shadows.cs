using UnityEngine;
using UnityEngine.Rendering;

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

    const int maxShadowedDirectionalLightCount = 4;

    //���ڻ�ȡ��ǰ֧����Ӱ�ķ����Դ��һЩ��Ϣ
    struct ShadowedDirectionalLight
    {        
        //��ǰ��Դ���������²������ΪCullingResults�й�Դ������(Ҳ��Lighting���µĹ�Դ���������Ƕ���ͳһ�ģ��ǳ�����~��
        public int visibleLightIndex;
    }
    //��Ȼ����Ŀǰ����Դ��Ϊ1������Ȼ������洢����Ϊ���������������~
    ShadowedDirectionalLight[] ShadowedDirectionalLights =
        new ShadowedDirectionalLight[maxShadowedDirectionalLightCount];
    //��ǰ��������ϵķ����Դ��
    int ShadowedDirectionalLightCount;

    static int dirShadowAtlasId = Shader.PropertyToID("_DirectionalShadowAtlas");


    public void Setup(ScriptableRenderContext context, CullingResults cullingResults,
        ShadowSettings settings)
    {
        this.context = context;
        this.cullingResults = cullingResults;
        this.settings = settings;

        ShadowedDirectionalLightCount = 0;
    }

    // ÿִ֡�У�����Ϊlight����shadow altas��shadowMap����Ԥ��һƬ�ռ�����Ⱦ��Ӱ��ͼ��ͬʱ�洢һЩ������Ҫ��Ϣ
    public void ReserveDirectionalShadows(Light light, int visibleLightIndex) 
    {
        // ���ù�Դ�����������ֵ   
        // ֻ���ÿ�����Ӱ����Ӱǿ�ȴ���0�Ĺ�Դ
        // ���Բ���Ҫ��Ⱦ�κ���Ӱ�Ĺ�Դ��ͨ��cullingResults.GetShadowCasterBounds������
        if (ShadowedDirectionalLightCount < maxShadowedDirectionalLightCount && 
            light.shadows != LightShadows.None && 
            light.shadowStrength > 0f && 
            cullingResults.GetShadowCasterBounds(visibleLightIndex, out Bounds b))

        {
            ShadowedDirectionalLights[ShadowedDirectionalLightCount++] =
                new ShadowedDirectionalLight
                {
                    visibleLightIndex = visibleLightIndex
                };
        }
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

        int split = ShadowedDirectionalLightCount <= 1 ? 1 : 2;
        int tileSize = atlasSize / split;

        for (int i = 0; i < ShadowedDirectionalLightCount; i++)
        {
            RenderDirectionalShadows(i, split, tileSize);
        }

        buffer.EndSample(bufferName);
        ExecuteBuffer();
    }

    /// <summary>
    /// ��Ⱦ������Դ����Ӱ��ͼ��ShadowAtlas��
    /// </summary>
    /// <param name="index">��Դ������</param>
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
        //ʹ��Unity�ṩ�Ľӿ���Ϊ�����Դ���������Ⱦ��Ӱ��ͼ�õ�VP�����splitData
        cullingResults.ComputeDirectionalShadowMatricesAndCullingPrimitives(light.visibleLightIndex,
            0, 1, Vector3.zero,
            tileSize, 0f,
            out Matrix4x4 viewMatrix, out Matrix4x4 projectionMatrix, out ShadowSplitData splitData);
        //splitData����Ͷ����Ӱ����Ӧ����α��ü�����Ϣ��������Ҫ�������ݸ�shadowSettings
        shadowSettings.splitData = splitData;
        SetTileViewport(index, split, tileSize);
        //����ǰVP��������Ϊ�������VP����׼����Ⱦ��Ӱ��ͼ
        buffer.SetViewProjectionMatrices(viewMatrix, projectionMatrix);
        ExecuteBuffer();
        //ʹ��context.DrawShadows����Ⱦ��Ӱ��ͼ������Ҫ����һ��shadowSettings
        context.DrawShadows(ref shadowSettings);
    }

    /// <summary>
    /// ���õ�ǰҪ��Ⱦ��Tile����
    /// </summary>
    /// <param name="index">Tile����</param>
    /// <param name="split">Tileһ�������ϵ�����</param>
    /// <param name="tileSize">һ��Tile�Ŀ�ȣ��߶ȣ�</param>
    void SetTileViewport(int index, int split, float tileSize)
    {
        Vector2 offset = new Vector2(index % split, index / split);
        buffer.SetViewport(new Rect(
            offset.x * tileSize, offset.y * tileSize, tileSize, tileSize
        ));
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