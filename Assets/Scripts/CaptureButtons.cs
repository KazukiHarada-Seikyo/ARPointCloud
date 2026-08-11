using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 下部のボタン列の見た目を、撮影の状態に合わせて動かす。
///
/// カメラアプリと同じ約束事にしてある。
///   待機中: 赤い丸
///   録画中: 赤い角丸の四角（少し小さくなる）
///
/// この「丸→四角」は説明が要らない合図で、初見のテスターでも意味が分かる。
/// 逆に言うと、ここを独自の見た目にすると途端に分からなくなる。
///
/// 使い方は UI_SETUP.md を参照。
/// </summary>
public class CaptureButtons : MonoBehaviour
{
    [SerializeField] private FrameCapture _capture;

    [Header("録画ボタン")]
    [Tooltip("赤い内側の丸。RecordButton の子に置く")]
    [SerializeField] private RectTransform _recordInner;

    [Tooltip("内側の丸の Image。角の丸みを差し替えるために要る")]
    [SerializeField] private Image _recordInnerImage;

    [Tooltip("待機中に使う丸いスプライト（Knob）")]
    [SerializeField] private Sprite _circleSprite;

    [Tooltip("録画中に使う角丸のスプライト（UISprite）")]
    [SerializeField] private Sprite _roundedSprite;

    [SerializeField] private float _idleSize = 150f;
    [SerializeField] private float _recordingSize = 78f;
    [SerializeField] private float _transitionSeconds = 0.18f;

    [Header("メモボタン")]
    [Tooltip("メモボタンの中の文字。いま何が選ばれているかを出す")]
    [SerializeField] private TextMeshProUGUI _noteLabel;

    private float _t;
    private bool _wasRecording;

    private void Reset()
    {
        _idleSize = 150f;
        _recordingSize = 78f;
    }

    private void Update()
    {
        bool recording = _capture != null && _capture.IsRecording;

        if (recording != _wasRecording)
        {
            _wasRecording = recording;
            SwapSprite(recording);
        }

        // 目標へなめらかに寄せる。急に切り替わると安っぽく見える
        float target = recording ? 1f : 0f;
        _t = _transitionSeconds <= 0f
            ? target
            : Mathf.MoveTowards(_t, target, Time.unscaledDeltaTime / _transitionSeconds);

        if (_recordInner != null)
        {
            float s = Mathf.Lerp(_idleSize, _recordingSize, _t);
            _recordInner.sizeDelta = new Vector2(s, s);
        }

        if (_noteLabel != null && _capture != null)
        {
            string note = _capture.Note;
            if (_noteLabel.text != note) _noteLabel.text = note;

            // 未設定のときだけ控えめにして、選ばれていることを分かりやすくする
            _noteLabel.color = note == "-"
                ? new Color(1f, 1f, 1f, 0.45f)
                : new Color(1f, 0.76f, 0.29f, 1f);
        }
    }

    private void SwapSprite(bool recording)
    {
        if (_recordInnerImage == null) return;

        Sprite s = recording ? _roundedSprite : _circleSprite;
        if (s != null) _recordInnerImage.sprite = s;
    }
}
