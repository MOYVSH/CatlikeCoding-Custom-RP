using UnityEngine;

//�������������Ӱ����ѡ�������
[System.Serializable]
public class ShadowSettings
{
    //maxDistance������Ұ�ڶ��Χ�ᱻ��Ⱦ����Ӱ��ͼ�ϣ����������������maxDistance�����岻�ᱻ��Ⱦ����Ӱ��ͼ��
    //������߼��²����£�
    //1.����maxDistance�����������Զƽ�棩�õ�һ��BoundingBox��Ҳ�����Ǹ����ͣ������BoundingBox����������Ҫ��Ⱦ��Ӱ������
    //2.�������BoundingBox��Ҳ�����Ǹ����ͣ��ͷ����Դ�ķ���ȷ����Ⱦ��Ӱ��ͼ�õ��������������׶�壬��Ⱦ��Ӱ��ͼ
    [Min(0f)] public float maxDistance = 100f;

    //��Ӱ��ͼ�����гߴ磬ʹ��ö�ٷ�ֹ����������ֵ����ΧΪ256-8192��
    public enum TextureSize
    {
        _256 = 256,
        _512 = 512,
        _1024 = 1024,
        _2048 = 2048,
        _4096 = 4096,
        _8192 = 8192
    }

    //���巽���Դ����Ӱ��ͼ����
    [System.Serializable]
    public struct Directional
    {
        public TextureSize atlasSize;
    }

    //����һ��1024��С��Directional Shadow Map
    public Directional directional = new Directional()
    {
        atlasSize = TextureSize._1024
    };
}