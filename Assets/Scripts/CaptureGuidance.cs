using Google.XR.ARCoreExtensions;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// 「いま撮ってよいか」を判定して、そのつど何をすべきかを言葉にする。
///
/// MonoBehaviourではない。GeospatialStatusDisplay が持って毎フレーム呼ぶ。
/// Inspectorの接続を増やさないための作り。
///
/// --------------------------------------------------------------------
/// しきい値の根拠(すべて実測から)
///
///   方位精度  VPS成立時 2.82度 / 不成立時 27.8度。到着直後は42度で、
///             10秒ほど歩くと17.5度まで改善する。
///             → 5度以下なら成立、15度超は不成立とみなす
///
///   水平精度  VPS成立時 1.30m / 不成立時 7.03m。
///             精度値は68%信頼半径なので、3回に1回はこの外に出る
///             → 2m以下なら良好、5m超は悪い
///
///   角速度    中央値22度/秒、最大101度/秒(手持ちでの実測)。
///             速いコマはブレるが端末側では捨てない。警告だけ出す
/// --------------------------------------------------------------------
/// </summary>
public class CaptureGuidance
{
    public enum Level
    {
        Blocked,    // 撮れない
        Wait,       // もう少し待つ
        Caution,    // 撮れるが条件が悪い
        Ready,      // 撮ってよい
        Recording,  // 録画中
    }

    // --- しきい値 ---
    private const float YawGood = 5.0f;
    private const float YawPoor = 15.0f;
    private const float HorizGood = 2.0f;
    private const float HorizPoor = 5.0f;
    private const float AngularSpeedWarn = 60.0f;

    /// <summary>精度値がこの秒数だけ動かなければ、外挿を疑う</summary>
    private const float FrozenSeconds = 2.0f;

    // --- 結果 ---
    public Level level;
    public string headline = "";
    public string advice = "";

    public bool sessionOk;
    public bool earthOk;
    public float yawAccuracy = float.NaN;
    public float horizAccuracy = float.NaN;

    /// <summary>精度値が固まっている＝測定ではなく外挿の疑い</summary>
    public bool extrapolating;
    public float frozenSeconds;

    // --- 外挿の検出に使う履歴 ---
    private double _lastAccH, _lastAccV, _lastAccYaw;
    private float _frozenSince = -1f;
    private bool _hasLast;

    public void Tick(AREarthManager earth, FrameCapture capture)
    {
        sessionOk = ARSession.state == ARSessionState.SessionTracking;
        earthOk = earth != null && earth.EarthTrackingState == TrackingState.Tracking;

        yawAccuracy = float.NaN;
        horizAccuracy = float.NaN;

        if (earthOk)
        {
            var pose = earth.CameraGeospatialPose;
            yawAccuracy = (float)pose.OrientationYawAccuracy;
            horizAccuracy = (float)pose.HorizontalAccuracy;
            UpdateExtrapolation(pose);
        }
        else
        {
            _hasLast = false;
            _frozenSince = -1f;
            extrapolating = false;
            frozenSeconds = 0f;
        }

        Decide(capture);
    }

    /// <summary>
    /// 精度値がまったく動かない区間は、測定ではなく前回値の引き伸ばし(外挿)。
    /// ARCoreはそれを教えてくれないので、値が固まっていることから推定する。
    ///
    /// CSVには書かない。精度値そのものが記録されているので、
    /// PC側で同じ判定ができる。ここは撮影中に気づくための表示専用。
    /// </summary>
    private void UpdateExtrapolation(GeospatialPose pose)
    {
        bool same = _hasLast
                    && pose.HorizontalAccuracy == _lastAccH
                    && pose.VerticalAccuracy == _lastAccV
                    && pose.OrientationYawAccuracy == _lastAccYaw;

        if (same)
        {
            if (_frozenSince < 0f) _frozenSince = Time.realtimeSinceStartup;
            frozenSeconds = Time.realtimeSinceStartup - _frozenSince;
        }
        else
        {
            _frozenSince = -1f;
            frozenSeconds = 0f;
        }

        extrapolating = frozenSeconds >= FrozenSeconds;

        _lastAccH = pose.HorizontalAccuracy;
        _lastAccV = pose.VerticalAccuracy;
        _lastAccYaw = pose.OrientationYawAccuracy;
        _hasLast = true;
    }

    private void Decide(FrameCapture capture)
    {
        // 録画中は別の案内に切り替える
        if (capture != null && capture.IsRecording)
        {
            level = Level.Recording;
            headline = "録画中";

            if (capture.LastAngularSpeed > AngularSpeedWarn)
                advice = "速すぎます。もっとゆっくり";
            else if (capture.DroppedCount > capture.SavedCount / 10 &&
                     capture.SavedCount > 30)
                advice = "保存が追いついていません";
            else
                advice = "ゆっくり歩きながら、同じ場所を別の角度からも映してください";
            return;
        }

        // --- 撮れない条件から順に見る ---
        if (capture == null)
        {
            level = Level.Blocked;
            headline = "設定が足りません";
            advice = "FrameCapture がつながっていません";
            return;
        }

        if (!sessionOk)
        {
            level = Level.Wait;
            headline = "準備中";
            advice = "スマホをゆっくり左右に動かして、周りの景色を映してください";
            return;
        }

        if (!capture.IsHighestResolution)
        {
            level = Level.Blocked;
            headline = "解像度が低いままです";
            advice = $"現在 {capture.ResolutionText}。数秒待つと自動で切り替わります";
            return;
        }

        if (capture.FreeMegabytes >= 0 &&
            capture.FreeMegabytes < capture.MinFreeMegabytes)
        {
            level = Level.Blocked;
            headline = "空き容量が足りません";
            advice = $"残り {capture.FreeMegabytes:F0}MB。"
                     + $"{capture.MinFreeMegabytes}MB以上あけてください";
            return;
        }

        if (!earthOk)
        {
            level = Level.Wait;
            headline = "位置を取得中";
            advice = "空が見える場所で、建物や看板を映してください";
            return;
        }

        // --- ここから先は撮れる。質の話 ---
        if (extrapolating)
        {
            level = Level.Caution;
            headline = "位置が更新されていません";
            advice = $"{frozenSeconds:F0}秒間、精度の値が動いていません。"
                     + "測っていない可能性があります。歩いて場所を変えてください";
            return;
        }

        if (yawAccuracy > YawPoor)
        {
            level = Level.Wait;
            headline = "方位が定まっていません";
            advice = $"現在 {yawAccuracy:F1}度。"
                     + "10秒ほど歩いて、周りの建物を映すと下がります";
            return;
        }

        if (yawAccuracy > YawGood || horizAccuracy > HorizPoor)
        {
            level = Level.Caution;
            headline = "撮れますが精度は不十分";
            advice = $"方位 {yawAccuracy:F1}度 / 水平 {horizAccuracy:F1}m。"
                     + "もう少し歩くと良くなることがあります";
            return;
        }

        level = Level.Ready;
        headline = "撮影できます";
        advice = "録画を押して、ゆっくり歩きながらかざしてください";
    }

    // ------------------------------------------------------------
    // 画面表示の補助
    // ------------------------------------------------------------

    public string LevelColor => level switch
    {
        Level.Ready => "#4CDE6A",
        Level.Recording => "#FF5A5A",
        Level.Caution => "#FFC24B",
        Level.Wait => "#7FB6FF",
        _ => "#FF5A5A",
    };

    /// <summary>チェック項目1行分。○△× と色を返す</summary>
    public static string Mark(int state)
    {
        return state switch
        {
            2 => "<color=#4CDE6A>○</color>",
            1 => "<color=#FFC24B>△</color>",
            _ => "<color=#FF5A5A>×</color>",
        };
    }

    public int SessionState => sessionOk ? 2 : 0;
    public int EarthState => earthOk ? 2 : 0;

    public int YawState =>
        float.IsNaN(yawAccuracy) ? 0 :
        yawAccuracy <= YawGood ? 2 :
        yawAccuracy <= YawPoor ? 1 : 0;

    public int HorizState =>
        float.IsNaN(horizAccuracy) ? 0 :
        horizAccuracy <= HorizGood ? 2 :
        horizAccuracy <= HorizPoor ? 1 : 0;
}
