using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Image を「計算で描いた円」に差し替える。
///
/// Unity内蔵の Knob スプライトは32px程度しかないため、200pxのボタンに使うと
/// 輪郭がギザギザになる。このコンポーネントを付けると、大きさに関係なく
/// 輪郭が滑らかになる。
///
/// マテリアルは実行時に作るので、.mat を用意する必要はない。
/// Image の Source Image は空でよい（あっても無視される）。
///
/// ※ Shader.Find で探すため、Project Settings → Graphics →
///    Always Included Shaders に ARPointCloud/UICircle を登録すること。
/// </summary>
[RequireComponent(typeof(Image))]
[ExecuteAlways]
public class UICircleImage : MonoBehaviour
{
    [Tooltip("0 なら塗りつぶした円。0.06 くらいで細いリングになる")]
    [SerializeField, Range(0f, 0.5f)] private float _ringWidth;

    [Tooltip("輪郭のなめらかさ。大きいとぼやける")]
    [SerializeField, Range(0.5f, 3f)] private float _softness = 1.2f;

    private Material _material;
    private Image _image;

    private void OnEnable()
    {
        Apply();
    }

    private void OnValidate()
    {
        Apply();
    }

    private void OnDestroy()
    {
        if (_material != null)
        {
            if (Application.isPlaying) Destroy(_material);
            else DestroyImmediate(_material);
        }
    }

    private void Apply()
    {
        if (_image == null) _image = GetComponent<Image>();
        if (_image == null) return;

        if (_material == null)
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

            _material = new Material(shader) { hideFlags = HideFlags.DontSave };
        }

        _material.SetFloat("_RingWidth", _ringWidth);
        _material.SetFloat("_Softness", _softness);
        _image.material = _material;
    }
}
