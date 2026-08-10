using Google.XR.ARCoreExtensions;
using TMPro;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class GeospatialStatusDisplay : MonoBehaviour
{
    [SerializeField] private AREarthManager _earthManager;
    [SerializeField] private Camera _arCamera;
    [SerializeField] private TextMeshProUGUI _text;
    [SerializeField] private GeospatialCsvLogger _logger;
    [SerializeField] private VpsCoverageChecker _coverage;
    [SerializeField] private CameraConfigLister _configLister;

    private void Start()
    {
        Input.location.Start();
    }

    private void Update()
    {
        // 映像設定を確認しているときは、そちらに画面を明け渡す
        if (_configLister != null && !string.IsNullOrEmpty(_configLister.Report))
        {
            _text.text = _configLister.Report;
            return;
        }

        // 1段目：端末が対応しているか
        var supported = _earthManager.IsGeospatialModeSupported(GeospatialMode.Enabled);

        // 2段目・3段目
        var earthState = _earthManager.EarthState;
        var trackingState = _earthManager.EarthTrackingState;

        // 4段目：追跡中のときだけ値が意味を持つ
        string geoInfo;
        if (trackingState == TrackingState.Tracking)
        {
            var pose = _earthManager.CameraGeospatialPose;
            geoInfo =
                $"緯度     : {pose.Latitude:F7}\n" +
                $"経度     : {pose.Longitude:F7}\n" +
                $"楕円体高 : {pose.Altitude:F2} m\n" +
                $"水平精度 : {pose.HorizontalAccuracy:F2} m\n" +
                $"垂直精度 : {pose.VerticalAccuracy:F2} m\n" +
                $"方位精度 : {pose.OrientationYawAccuracy:F2} 度\n" +
                $"方位(参考): {HeadingFromEun(pose.EunRotation):F1} 度";
        }
        else
        {
            geoInfo = "(まだ位置が取れていません)";
        }

        var local = _arCamera.transform.position;

        _text.text =
            $"REC: {(_logger.IsRecording ? "● " + _logger.FileName : "停止中")}  CSV:{_logger.FileCount}件\n" +
            $"{_logger.LastMessage}\n" +
            $"VPS: {_coverage.Report}\n" +
            $"[1] 端末対応  : {supported}\n" +
            $"[2] EarthState: {earthState}\n" +
            $"[3] Tracking  : {trackingState}\n" +
            $"    ARSession : {ARSession.state}\n" +
            $"    位置情報  : {Input.location.status}\n" +
            $"--------------------------\n" +
            $"{geoInfo}\n" +
            $"--------------------------\n" +
            $"ローカル X:{local.x:F2} Y:{local.y:F2} Z:{local.z:F2}";
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