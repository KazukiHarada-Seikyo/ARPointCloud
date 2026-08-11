using TMPro;
using UnityEngine;

/// <summary>
/// 下部のボタン列の見た目を、撮影の状態に合わせて動かす。
///
/// カメラアプリと同じ約束事にしてある。
///   待機中: 赤い丸（大きい）
///   録画中: 赤い角丸の四角（小さい）
///
/// この「丸→四角」は説明が要らない合図で、初見のテスターでも意味が分かる。
/// 逆に言うと、ここを独自の見た目にすると途端に分からなくなる。
///
/// 形は UICircle の Roundness を動かして変える。スプライトの差し替えではなく
/// 計算で描いているので、変形の途中も輪郭が滑らかなまま。
///
/// 使い方は UI_SETUP.md を参照。
/// </summary>
public class CaptureButtons : MonoBehaviour
{
    [SerializeField] private FrameCapture _capture;

    [Header("録画ボタン")]
    [Tooltip("赤い内側の丸。RecordButton の子に置いた UICircle")]
    [SerializeField] private UICircle _recordInner;

    [Tooltip("内側の丸の RectTransform。大きさを変えるために要る")]
    [SerializeField] private RectTransform _recordInnerRect;

    [Header("形と大きさ")]
    [SerializeField] private float _idleSize = 150f;
    [SerializeField] private float _recordingSize = 78f;

    [Tooltip("待機中の丸み。1 が真円")]
    [SerializeField, Range(0f, 1f)] private float _idleRoundness = 1f;

    [Tooltip("録画中の丸み。0.3 くらいが角丸の四角")]
    [SerializeField, Range(0f, 1f)] private float _recordingRoundness = 0.3f;

    [SerializeField] private float _transitionSeconds = 0.18f;

    [Header("メモボタン")]
    [Tooltip("メモボタンの中の文字。いま何が選ばれているかを出す")]
    [SerializeField] private TextMeshProUGUI _noteLabel;

    [SerializeField] private Color _noteIdleColor = new Color(1f, 1f, 1f, 0.45f);
    [SerializeField] private Color _noteSetColor = new Color(1f, 0.76f, 0.29f, 1f);

    private float _t;

    private void Update()
    {
        bool recording = _capture != null && _capture.IsRecording;

        // 目標へなめらかに寄せる。急に切り替わると安っぽく見える
        float target = recording ? 1f : 0f;
        _t = _transitionSeconds <= 0f
            ? target
            : Mathf.MoveTowards(_t, target, Time.unscaledDeltaTime / _transitionSeconds);

        if (_recordInnerRect != null)
        {
            float s = Mathf.Lerp(_idleSize, _recordingSize, _t);
            _recordInnerRect.sizeDelta = new Vector2(s, s);
        }

        if (_recordInner != null)
        {
            _recordInner.Roundness =
                Mathf.Lerp(_idleRoundness, _recordingRoundness, _t);
        }

        if (_noteLabel != null && _capture != null)
        {
            string note = _capture.Note;
            if (_noteLabel.text != note) _noteLabel.text = note;

            // 未設定のときだけ控えめにして、選ばれていることを分かりやすくする
            _noteLabel.color = note == "-" ? _noteIdleColor : _noteSetColor;
        }
    }
}
