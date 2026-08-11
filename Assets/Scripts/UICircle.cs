using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 円・リングを描くUI部品。Image の代わりに使う。
///
/// --------------------------------------------------------------------
/// なぜ Image を使わないのか
///
/// Image はスプライトを貼るための部品なので、頂点のUVがスプライトの
/// 位置に合わせて決まる。スプライトが入っていると 0〜1 にならないため、
/// 「中心からの距離」を測る計算が成立せず、円ではなく四角になる。
///
/// この部品は四角形とUVを自分で作るので、その問題が起きない。
/// スプライトも不要になる（Knob のギザギザもここで消える）。
/// --------------------------------------------------------------------
///
/// ※ Shader.Find で探すため、Project Settings → Graphics →
///    Always Included Shaders に ARPointCloud/UICircle を登録すること。
/// </summary>
[AddComponentMenu("UI/AR Point Cloud/UI Circle")]
[RequireComponent(typeof(CanvasRenderer))]
[ExecuteAlways]
public class UICircle : MaskableGraphic
{
    [Tooltip("1 で円、0.35 くらいで角丸の四角、0 で四角")]
    [SerializeField, Range(0f, 1f)] private float _roundness = 1f;

    [Tooltip("0 なら塗りつぶし。0.06 くらいで細いリングになる")]
    [SerializeField, Range(0f, 0.5f)] private float _ringWidth;

    [Tooltip("輪郭のなめらかさ。大きいとぼやける")]
    [SerializeField, Range(0.5f, 3f)] private float _softness = 1.2f;

    private Material _instance;

    /// <summary>1 で円、0.35 くらいで角丸の四角。動かすとなめらかに変形する</summary>
    public float Roundness
    {
        get => _roundness;
        set
        {
            if (Mathf.Approximately(_roundness, value)) return;
            _roundness = value;
            ApplyMaterial();
        }
    }

    public float RingWidth
    {
        get => _ringWidth;
        set { _ringWidth = value; ApplyMaterial(); }
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        ApplyMaterial();
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        DestroyInstance();
    }

#if UNITY_EDITOR
    protected override void OnValidate()
    {
        base.OnValidate();
        ApplyMaterial();
        SetVerticesDirty();
    }
#endif

    private void DestroyInstance()
    {
        if (_instance == null) return;
        if (Application.isPlaying) Destroy(_instance);
        else DestroyImmediate(_instance);
        _instance = null;
    }

    private void ApplyMaterial()
    {
        if (_instance == null)
        {
            var shader = Shader.Find("ARPointCloud/UICircle");
            if (shader == null)
            {
                Debug.LogError(
                    "シェーダ ARPointCloud/UICircle が見つかりません。\n" +
                    "Project Settings → Graphics → Always Included Shaders に" +
                    "登録してください");
                return;
            }

            _instance = new Material(shader) { hideFlags = HideFlags.DontSave };
        }

        _instance.SetFloat("_Roundness", _roundness);
        _instance.SetFloat("_RingWidth", _ringWidth);
        _instance.SetFloat("_Softness", _softness);

        material = _instance;
        SetMaterialDirty();
    }

    /// <summary>
    /// 四角形を1枚だけ作る。UVは必ず 0〜1 にする。
    /// シェーダはこのUVから中心までの距離を測って円に抜く。
    /// </summary>
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        Rect r = GetPixelAdjustedRect();
        Color32 c = color;

        vh.AddVert(new Vector3(r.xMin, r.yMin), c, new Vector2(0f, 0f));
        vh.AddVert(new Vector3(r.xMin, r.yMax), c, new Vector2(0f, 1f));
        vh.AddVert(new Vector3(r.xMax, r.yMax), c, new Vector2(1f, 1f));
        vh.AddVert(new Vector3(r.xMax, r.yMin), c, new Vector2(1f, 0f));

        vh.AddTriangle(0, 1, 2);
        vh.AddTriangle(2, 3, 0);
    }
}
