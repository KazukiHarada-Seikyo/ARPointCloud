using System;
using System.Globalization;
using System.IO;
using System.Text;
using Google.XR.ARCoreExtensions;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// 撮影せずにログだけ取るためのロガー。
/// 階を変えた楕円体高の検証など、画像が要らない作業で使う。
///
/// 撮影中は FrameCapture の frames.csv 一本に寄せる。
/// 列が包含関係にあるうえ、書き込みが競合して保存が詰まるため、
/// 両者は相互ガードで排他にしてある。
/// </summary>
public class GeospatialCsvLogger : MonoBehaviour
{
    [SerializeField] private AREarthManager _earthManager;
    [SerializeField] private Camera _arCamera;

    [Tooltip("同時に走らせないための相互ガード")]
    [SerializeField] private FrameCapture _frameCapture;

    private StreamWriter _writer;
    private string _path;
    private int _frame;

    public bool IsRecording => _writer != null;
    public string FileName => _path == null ? "-" : Path.GetFileName(_path);

    /// <summary>直近の操作結果。端末の画面に出して確認するために持っている</summary>
    public string LastMessage { get; private set; } = "";

    /// <summary>保存済みCSVの本数</summary>
    public int FileCount { get; private set; }

    private void Start()
    {
        RefreshFileCount();
    }

    private void RefreshFileCount()
    {
        FileCount = Directory.GetFiles(
            Application.persistentDataPath, "geolog_*.csv").Length;
    }

    // ------------------------------------------------------------
    // 録画
    // ------------------------------------------------------------

    public void ToggleRecording()
    {
        if (IsRecording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        // 撮影中は frames.csv 側が同じ値をすべて書いている。
        // 二重に書くと書き込みが競合して、写真の保存が詰まる
        if (_frameCapture != null && _frameCapture.IsRecording)
        {
            LastMessage = "撮影中です。frames.csv に記録されています";
            Debug.LogWarning(LastMessage);
            return;
        }

        _path = Path.Combine(
            Application.persistentDataPath,
            $"geolog_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        _writer = new StreamWriter(_path, false, Encoding.UTF8);
        _writer.WriteLine(
            "unix_ms,elapsed_s,frame," +
            "session_state,earth_state,tracking_state," +
            "local_px,local_py,local_pz,local_qx,local_qy,local_qz,local_qw," +
            "lat,lon,alt_ellipsoid," +
            "eun_qx,eun_qy,eun_qz,eun_qw," +
            "acc_h,acc_v,acc_yaw");

        _frame = 0;
        LastMessage = $"録画開始 {Path.GetFileName(_path)}";
        Debug.Log(LastMessage);
    }

    private void StopRecording()
    {
        if (_writer == null) return;

        _writer.Flush();
        _writer.Dispose();
        _writer = null;

        RefreshFileCount();
        LastMessage = $"録画停止 {_frame}行";
        Debug.Log(LastMessage);
    }

    // ------------------------------------------------------------
    // 共有
    // ------------------------------------------------------------

    /// <summary>共有シートを出して、Driveなどに保存させる</summary>
    public void ShareLatestFile()
    {
        // 書きかけのまま渡すと途中で切れたファイルが飛ぶので、必ず閉じる
        if (IsRecording) StopRecording();
        RefreshFileCount();

        var dir = new DirectoryInfo(Application.persistentDataPath);
        var files = dir.GetFiles("geolog_*.csv");

        if (files.Length == 0)
        {
            LastMessage = "CSVがありません。先に録画してください";
            Debug.LogWarning(LastMessage);
            return;
        }

        FileInfo latest = files[0];
        foreach (var f in files)
        {
            if (f.LastWriteTimeUtc > latest.LastWriteTimeUtc) latest = f;
        }

        LastMessage = $"共有 {latest.Name} / {latest.Length}B";
        Debug.Log(LastMessage);

        new NativeShare()
            .AddFile(latest.FullName)
            .SetSubject("Geospatial log")
            .SetTitle("保存先を選んでください")
            .Share();
    }

    // ------------------------------------------------------------
    // 記録本体
    // ------------------------------------------------------------

    private void Update()
    {
        if (_writer == null) return;

        var c = CultureInfo.InvariantCulture;
        var t = _arCamera.transform;
        Vector3 p = t.position;
        Quaternion q = t.rotation;

        var tracking = _earthManager.EarthTrackingState;
        bool ok = tracking == TrackingState.Tracking;
        var pose = ok ? _earthManager.CameraGeospatialPose : new GeospatialPose();
        Quaternion eun = pose.EunRotation;

        // 追跡できていない行は空欄にする。ゼロを書くと緯度0度・経度0度という
        // 実在する座標として読まれてしまうため
        string G(bool valid, double v, string f) => valid ? v.ToString(f, c) : "";

        _writer.WriteLine(string.Join(",", new[]
        {
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(c),
            Time.realtimeSinceStartup.ToString("F3", c),
            (_frame++).ToString(c),
            ARSession.state.ToString(),
            _earthManager.EarthState.ToString(),
            tracking.ToString(),
            p.x.ToString("F4", c), p.y.ToString("F4", c), p.z.ToString("F4", c),
            q.x.ToString("F6", c), q.y.ToString("F6", c),
            q.z.ToString("F6", c), q.w.ToString("F6", c),
            G(ok, pose.Latitude,  "F8"),
            G(ok, pose.Longitude, "F8"),
            G(ok, pose.Altitude,  "F3"),
            G(ok, eun.x, "F6"), G(ok, eun.y, "F6"),
            G(ok, eun.z, "F6"), G(ok, eun.w, "F6"),
            G(ok, pose.HorizontalAccuracy,     "F3"),
            G(ok, pose.VerticalAccuracy,       "F3"),
            G(ok, pose.OrientationYawAccuracy, "F3"),
        }));

        // 毎フレーム書くと重く、ためこみすぎると落ちたときに消える
        if (_frame % 60 == 0) _writer.Flush();
    }

    private void OnApplicationPause(bool paused)
    {
        // Androidはバックグラウンドのアプリを予告なく終了させる
        if (paused) _writer?.Flush();
    }

    private void OnApplicationQuit() => StopRecording();
}
