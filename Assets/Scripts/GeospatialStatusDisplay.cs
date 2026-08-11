using Google.XR.ARCoreExtensions;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// 撮影中の画面。
///
/// 上から順に「いま何をすべきか」→「準備できているか」→「細かい数値」。
/// テスターは上2つだけ見れば足りる。数値は不具合を追うときだけ要る。
///
/// UIオブジェクトを増やさず、TextMeshProのリッチテキストで色と大きさを付けている。
/// Inspectorの組み直しを避けるため。
/// </summary>
public class GeospatialStatusDisplay : MonoBehaviour
{
    [SerializeField] private AREarthManager _earthManager;
    [SerializeField] private Camera _arCamera;
    [SerializeField] private TextMeshProUGUI _text;
    [SerializeField] private GeospatialCsvLogger _logger;
    [SerializeField] private FrameCapture _capture;

    [Tooltip("特徴点の重ね描き。未設定でも動く")]
    [SerializeField] private PointCloudPreview _preview;

    [Tooltip("細かい数値を出すか。テスターに渡すときは切っておく")]
    [SerializeField] private bool _showDetail = true;

    private readonly CaptureGuidance _guidance = new CaptureGuidance();

    /// <summary>ボタンから呼ぶ。数値の表示を出し入れする</summary>
    public void ToggleDetail() => _showDetail = !_showDetail;

    private void Start()
    {
        Input.location.Start();
    }

    private void Update()
    {
        _guidance.Tick(_earthManager, _capture);

        _text.text = Headline() + Checklist() + (_showDetail ? Detail() : "");
    }

    // ------------------------------------------------------------
    // 1段目: いま何をすべきか
    // ------------------------------------------------------------

    private string Headline()
    {
        string c = _guidance.LevelColor;
        string s = $"<size=150%><color={c}>● {_guidance.headline}</color></size>\n";

        if (_guidance.level == CaptureGuidance.Level.Recording && _capture != null)
        {
            int sec = Mathf.FloorToInt(_capture.RecordingSeconds);
            s += $"<size=130%><color={c}>{sec / 60}:{sec % 60:00}"
                 + $"   {_capture.SavedCount}枚</color></size>\n";
        }

        // 特徴点の数は「認識できている量」の手応えになる。
        // ただしこれは完成する点群ではないので、そう読める書き方は避ける
        if (_preview != null && _preview.PointCount > 0)
        {
            s += $"<size=90%><color=#8FD6A6>認識中の目印 "
                 + $"{_preview.PointCount:N0}</color></size>\n";
        }

        s += $"{_guidance.advice}\n";
        s += "<color=#666666>────────────────</color>\n";
        return s;
    }

    // ------------------------------------------------------------
    // 2段目: 準備できているか
    // ------------------------------------------------------------

    private string Checklist()
    {
        string s = "";

        s += Line(_guidance.SessionState, "端末の追跡",
                  _guidance.sessionOk ? "良好" : "まだ");

        s += Line(_guidance.EarthState, "位置の取得",
                  _guidance.earthOk ? "良好" : "待機中");

        s += Line(_guidance.YawState, "方位の精度",
                  float.IsNaN(_guidance.yawAccuracy)
                      ? "-" : $"{_guidance.yawAccuracy:F1} 度");

        s += Line(_guidance.HorizState, "位置の精度",
                  float.IsNaN(_guidance.horizAccuracy)
                      ? "-" : $"{_guidance.horizAccuracy:F1} m");

        if (_capture != null)
        {
            s += Line(_capture.IsHighestResolution ? 2 : 0, "解像度",
                      _capture.ResolutionText);

            bool spaceOk = _capture.FreeMegabytes < 0
                           || _capture.FreeMegabytes >= _capture.MinFreeMegabytes;
            s += Line(spaceOk ? 2 : 0, "空き容量",
                      _capture.FreeMegabytes < 0
                          ? "-" : $"{_capture.FreeMegabytes / 1024f:F1} GB");

            s += $"  <color=#888888>メモ</color>   "
                 + $"<color=#FFC24B>{_capture.Note}</color>\n";
        }

        if (_guidance.extrapolating)
        {
            s += $"<color=#FFC24B>  ※ 精度の値が {_guidance.frozenSeconds:F0} 秒"
                 + "動いていません（外挿の疑い）</color>\n";
        }

        s += "<color=#666666>────────────────</color>\n";
        return s;
    }

    private static string Line(int state, string label, string value)
    {
        return $"  {CaptureGuidance.Mark(state)} <color=#CCCCCC>{label}</color>"
               + $"   {value}\n";
    }

    // ------------------------------------------------------------
    // 3段目: 細かい数値（不具合を追うとき用）
    // ------------------------------------------------------------

    private string Detail()
    {
        string s = "<size=80%><color=#999999>";

        if (_capture != null)
        {
            s += $"撮影 {(_capture.IsRecording ? _capture.SessionName : "停止中")}"
                 + $"  {_capture.TargetFps}fps 設定\n";
            s += $"保存 {_capture.SavedCount} / 見送り {_capture.DroppedCount}"
                 + $" / 未処理 {_capture.PendingConversions}"
                 + $" / 角速度 {_capture.LastAngularSpeed:F0} 度毎秒\n";
            s += $"{_capture.LastMessage}\n";
        }

        if (_logger != null)
        {
            s += $"LOG {(_logger.IsRecording ? "● " + _logger.FileName : "停止中")}"
                 + $"  CSV {_logger.FileCount}件\n";
        }

        s += $"ARSession {ARSession.state} / EarthState {_earthManager.EarthState}"
             + $" / 位置情報 {Input.location.status}\n";

        if (_earthManager.EarthTrackingState == TrackingState.Tracking)
        {
            var pose = _earthManager.CameraGeospatialPose;
            s += $"緯度 {pose.Latitude:F7}  経度 {pose.Longitude:F7}\n";
            s += $"楕円体高 {pose.Altitude:F2} m  垂直精度 {pose.VerticalAccuracy:F2} m\n";
            s += $"方位(参考) {HeadingFromEun(pose.EunRotation):F1} 度\n";
        }

        var local = _arCamera.transform.position;
        s += $"ローカル X:{local.x:F2} Y:{local.y:F2} Z:{local.z:F2}";
        s += "</color></size>";
        return s;
    }

    // EUN座標（X+=東, Y+=上, Z+=北）での前方向から、真北を0度とした方位角を出す
    // 端末を地面や空に向けているときは破綻するので、値はまだ参考扱い
    private float HeadingFromEun(Quaternion eunRotation)
    {
        Vector3 forward = eunRotation * Vector3.forward;
        float deg = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        return (deg + 360f) % 360f;
    }
}
