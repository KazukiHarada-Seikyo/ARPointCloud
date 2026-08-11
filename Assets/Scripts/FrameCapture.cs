using System;
using System.Globalization;
using System.IO;
using Google.XR.ARCoreExtensions;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// フェーズ1: 姿勢付きの連番JPEGを取り込む。
///
/// Step 1(1枚だけ保存) = RequestCapture()
/// Step 2(連続保存)    = ToggleRecording()
///
/// 保存先は撮影ごとのフォルダ:
///   persistentDataPath/rec_yyyyMMdd_HHmmss/
///     frames.csv
///     frame_&lt;unix_ms&gt;.jpg ...
/// </summary>
public class FrameCapture : MonoBehaviour
{
    [SerializeField] private ARCameraManager _cameraManager;
    [SerializeField] private Camera _arCamera;

    [Tooltip("frames.csv に地球座標を書くために使う")]
    [SerializeField] private AREarthManager _earthManager;

    [Tooltip("同時に走らせないための相互ガード。片方が動いている間はもう片方を止める")]
    [SerializeField] private GeospatialCsvLogger _csvLogger;

    [SerializeField, Range(70, 100)]
    [Tooltip("JPEG品質。90〜95で使う。落としすぎると特徴点の手がかりが減る")]
    private int _jpegQuality = 92;

    [SerializeField, Range(1, 30)]
    [Tooltip("保存レート(fps)。1080pで間に合うかを見て決める")]
    private int _targetFps = 10;

    [Tooltip("この空き容量(MB)を切ったら録画を自動で止める")]
    [SerializeField] private long _minFreeMegabytes = 500;

    [Tooltip("未処理の変換がこの数を超えたら、そのコマは見送る(輻輳よけ)")]
    [SerializeField] private int _maxPendingConversions = 4;

    [Header("屋外での安全策")]
    [Tooltip("撮影中に画面が消えないようにする。消えるとカメラも姿勢も止まる")]
    [SerializeField] private bool _keepScreenAwake = true;

    [Tooltip("縦持ちに固定する。姿勢と画像のずれ方が画面の向きで変わるため、" +
             "実データで検証できている Portrait から外れないようにする")]
    [SerializeField] private bool _lockPortrait = true;

    // ------------------------------------------------------------
    // 画面に出す情報
    // ------------------------------------------------------------

    /// <summary>直近の操作結果</summary>
    public string LastMessage { get; private set; } = "未撮影";

    /// <summary>
    /// 利用者に必ず伝えたいこと（録画を断った理由、削除の確認など）。
    ///
    /// LastMessage は録画中に毎フレーム書き換わるので、
    /// 「押したのに始まらない」ような場面の理由がすぐ流れてしまう。
    /// こちらは押したときだけ更新し、画面の目立つ位置に数秒出す。
    /// </summary>
    public string Notice { get; private set; } = "";

    /// <summary>Notice を出した時刻（Time.unscaledTime）</summary>
    public float NoticeAt { get; private set; } = -999f;

    private void SetNotice(string message)
    {
        Notice = message;
        NoticeAt = Time.unscaledTime;
        LastMessage = message;
        Debug.Log(message);
    }

    /// <summary>録画中かどうか（停止処理の最中は false になる）</summary>
    public bool IsRecording => _writer != null && !_stopRequested;

    /// <summary>今の撮影フォルダ名</summary>
    public string SessionName { get; private set; } = "-";

    /// <summary>今の撮影で保存できた枚数</summary>
    public int SavedCount { get; private set; }

    /// <summary>変換が間に合わずに見送った枚数。Step 3 でレートを決める材料</summary>
    public int DroppedCount { get; private set; }

    /// <summary>直近の角速度(度/秒)。画面でブレを警告するために使う</summary>
    public float LastAngularSpeed { get; private set; }

    /// <summary>未処理の変換。詰まりの目安</summary>
    public int PendingConversions => _pending;

    /// <summary>設定した保存レート</summary>
    public int TargetFps => _targetFps;

    /// <summary>録画開始からの経過秒。停止中は0</summary>
    public float RecordingSeconds =>
        _writer == null ? 0f : Time.realtimeSinceStartup - _recordStartTime;

    /// <summary>最高解像度で動いているか</summary>
    public bool IsHighestResolution => IsHighestResolutionActive(out _);

    /// <summary>今の解像度の表示用文字列</summary>
    public string ResolutionText
    {
        get { IsHighestResolutionActive(out string t); return t; }
    }

    /// <summary>空き容量(MB)。取得できないときは -1</summary>
    public float FreeMegabytes { get; private set; } = -1f;

    /// <summary>最低限必要な空き容量(MB)</summary>
    public long MinFreeMegabytes => _minFreeMegabytes;

    /// <summary>
    /// 撮影メモ。どの階で撮ったかなど、あとから思い出せない事実を残す。
    /// §6-1(高さの出どころ)の検証では、これが無いとデータの区別がつかない。
    /// </summary>
    public string Note { get; private set; } = "-";

    private static readonly string[] NoteChoices =
        { "-", "1F", "2F", "3F", "4F", "屋上", "地上", "屋内" };
    private int _noteIndex;

    /// <summary>ボタンから呼ぶ。押すたびに次のメモへ切り替わる</summary>
    public void CycleNote()
    {
        _noteIndex = (_noteIndex + 1) % NoteChoices.Length;
        Note = NoteChoices[_noteIndex];
    }

    // ------------------------------------------------------------
    // 内部状態
    // ------------------------------------------------------------

    private bool _captureRequested;      // 1枚だけ保存の要求
    private bool _stopRequested;         // 停止要求（未処理の変換を待っている間 true）
    private StreamWriter _writer;
    private string _sessionDir;
    private int _frame;
    private int _pending;                // 未処理の変換
    private long _lastSavedUnixMs = -1;
    private Texture2D _texture;
    private float _recordStartTime;

    // 空き容量の問い合わせは重いので、数秒に1回だけ更新する
    private float _nextFreeSpaceCheck;

    // ブレの目安(angular_speed_deg_s)を出すために、直前に保存したコマを覚えておく
    private bool _hasPrevPose;
    private Quaternion _prevRotation;
    private long _prevUnixMs;

    // 解像度の自動選択は、映像が流れ始めてからでないとできないので1回だけ試す
    private bool _resolutionSelected;

    // 削除の2回押し確認
    private const float DeleteConfirmSeconds = 6f;
    private string _deleteArmedFor;
    private float _deleteArmedAt = -999f;

    private const string CsvHeader =
        "unix_ms,frame_timestamp_ns,elapsed_s,frame," +
        "session_state,earth_state,tracking_state," +
        "local_px,local_py,local_pz,local_qx,local_qy,local_qz,local_qw," +
        "lat,lon,alt_ellipsoid,eun_qx,eun_qy,eun_qz,eun_qw,acc_h,acc_v,acc_yaw," +
        "filename,img_w,img_h,fx,fy,cx,cy," +
        "angular_speed_deg_s,screen_orientation,note";

    // ------------------------------------------------------------
    // ライフサイクル
    // ------------------------------------------------------------

    private void Awake()
    {
        // 画面が消えるとカメラも姿勢も止まる。屋外では数分かざし続けるので、
        // 端末側のスリープ設定に関係なく点けたままにする
        if (_keepScreenAwake) Screen.sleepTimeout = SleepTimeout.NeverSleep;

        // 縦持ちに固定する。
        //
        // AR Foundation が返す姿勢は画面の向きに合わせたもの、CPU画像は
        // センサーの向きそのまま。両者のずれ方は画面の向きで変わる。
        // 実データで検証できているのは Portrait だけ(点が10.7倍になった)なので、
        // 検証済みの経路から外れないように固定する。
        //
        // screen_orientation 列には記録し続けるので、あとから確かめられる
        if (_lockPortrait) Screen.orientation = ScreenOrientation.Portrait;
    }

    private void OnEnable()
    {
        if (_cameraManager != null) _cameraManager.frameReceived += OnFrameReceived;
    }

    private void OnDisable()
    {
        if (_cameraManager != null) _cameraManager.frameReceived -= OnFrameReceived;
    }

    private void Update()
    {
        // 空き容量の問い合わせは重いので、数秒に1回でよい。
        // 画面に出すためだけなので、精度も要らない
        if (Time.realtimeSinceStartup >= _nextFreeSpaceCheck)
        {
            _nextFreeSpaceCheck = Time.realtimeSinceStartup + 3f;
            long b = GetFreeBytes();
            FreeMegabytes = b < 0 ? -1f : b / 1024f / 1024f;
        }
    }

    private void OnApplicationPause(bool paused)
    {
        // Androidはバックグラウンドのアプリを予告なく終了させる
        if (paused) _writer?.Flush();
    }

    private void OnApplicationQuit() => CloseWriter();

    private void OnDestroy()
    {
        CloseWriter();
        if (_texture != null) Destroy(_texture);
    }

    // ------------------------------------------------------------
    // ボタンから呼ぶ
    // ------------------------------------------------------------

    /// <summary>次に届くフレームを1枚だけ保存する（動作確認用）</summary>
    public void RequestCapture()
    {
        if (_cameraManager == null)
        {
            LastMessage = "ARCameraManager が未設定です";
            return;
        }

        _captureRequested = true;
        LastMessage = "撮影要求 → 次のフレーム待ち";
    }

    /// <summary>連続保存の開始・停止</summary>
    public void ToggleRecording()
    {
        if (IsRecording) RequestStop();
        else StartRecording();
    }

    /// <summary>
    /// いちばん古い撮影フォルダを消す。押すたびに1つずつ古い順に消える。
    ///
    /// 取り消せないので2回押させる。1回目は消す対象と枚数を出すだけで、
    /// 続けてもう一度押したときに初めて消す。数秒放っておくと解除される。
    ///
    /// 古い順にしたのは、屋外で容量が尽きたときに「さっき撮ったものは
    /// 残したい」のが普通だから。
    /// </summary>
    public void DeleteOldestRecording()
    {
        // IsRecording は停止処理中に false になる。まだ書き込みが残っている
        // 可能性があるので、writer が閉じきるまでは触らせない
        if (_writer != null)
        {
            SetNotice("録画中は消せません。止めてから押してください");
            return;
        }

        var oldest = FindOldestRecording();
        if (oldest == null)
        {
            SetNotice("消せる撮影データがありません");
            _deleteArmedFor = null;
            return;
        }

        // 1回目、または対象が変わった、または時間切れ → 確認を出すだけ
        bool armed = _deleteArmedFor == oldest.Name
                     && Time.unscaledTime - _deleteArmedAt <= DeleteConfirmSeconds;

        if (!armed)
        {
            _deleteArmedFor = oldest.Name;
            _deleteArmedAt = Time.unscaledTime;

            int photos = oldest.GetFiles("*.jpg").Length;
            float mb = 0f;
            foreach (var f in oldest.GetFiles()) mb += f.Length / 1024f / 1024f;

            SetNotice($"もう一度押すと削除します\n"
                      + $"{oldest.Name} / 写真{photos}枚 / {mb:F0}MB");
            return;
        }

        // 2回目 → 実行
        _deleteArmedFor = null;

        float freedMb = 0f;
        foreach (var f in oldest.GetFiles()) freedMb += f.Length / 1024f / 1024f;

        try
        {
            oldest.Delete(true);
        }
        catch (Exception e)
        {
            SetNotice($"削除できません: {e.Message}");
            Debug.LogException(e);
            return;
        }

        // 空き容量の表示をすぐ更新する
        _nextFreeSpaceCheck = 0f;

        SetNotice($"削除しました {oldest.Name}\n{freedMb:F0}MB 空きました");
    }

    private DirectoryInfo FindOldestRecording()
    {
        var dir = new DirectoryInfo(Application.persistentDataPath);
        DirectoryInfo oldest = null;

        foreach (var d in dir.GetDirectories("rec_*"))
        {
            // 「いま書いている」フォルダだけ外す。
            // _sessionDir は停止後も残るので、これを無条件に外すと
            // 直前に撮ったものが永久に消せなくなる
            if (_writer != null && _sessionDir != null &&
                string.Equals(d.FullName, _sessionDir, StringComparison.Ordinal))
                continue;

            // 名前が rec_yyyyMMdd_HHmmss なので、文字列の並びが時刻の並びになる
            if (oldest == null || d.Name.CompareTo(oldest.Name) < 0) oldest = d;
        }

        return oldest;
    }

    /// <summary>
    /// 直近の撮影の frames.csv だけを共有シートに出す。
    ///
    /// 写真は数百MBあって現地では送れないが、CSVは数百KBなので送れる。
    /// 姿勢と精度がその場で確認できるので、条件の悪い場所で撮り続けて
    /// しまう事故を防げる。写真そのものはUSBで持ち帰る。
    /// </summary>
    public void ShareFramesCsv()
    {
        // 停止処理の途中はまだ書き込みが残っている。閉じきるまで待つ
        if (_writer != null)
        {
            SetNotice("録画中です。止めてから共有してください");
            return;
        }

        var dir = new DirectoryInfo(Application.persistentDataPath);
        DirectoryInfo latest = null;

        foreach (var d in dir.GetDirectories("rec_*"))
        {
            if (!File.Exists(Path.Combine(d.FullName, "frames.csv"))) continue;
            if (latest == null || d.Name.CompareTo(latest.Name) > 0) latest = d;
        }

        if (latest == null)
        {
            SetNotice("撮影データがありません");
            return;
        }

        string csv = Path.Combine(latest.FullName, "frames.csv");
        var info = new FileInfo(csv);
        int jpgCount = latest.GetFiles("*.jpg").Length;

        LastMessage = $"共有 {latest.Name}/frames.csv\n"
                      + $"{info.Length / 1024f:F0}KB / 写真{jpgCount}枚は端末に残ります";
        Debug.Log(LastMessage);

        new NativeShare()
            .AddFile(csv)
            .SetSubject($"frames.csv ({latest.Name})")
            .SetTitle("保存先を選んでください")
            .Share();
    }

    // ------------------------------------------------------------
    // 録画の開始と停止
    // ------------------------------------------------------------

    private void StartRecording()
    {
        if (_writer != null)
        {
            SetNotice("前回の停止処理がまだ終わっていません");
            return;
        }

        if (_cameraManager == null || _earthManager == null)
        {
            SetNotice("Inspectorの接続が足りません");
            return;
        }

        // 【ガード1】低解像度のまま撮ってしまう事故を防ぐ。
        // 起動のたびに640x480へ戻るので、ここで必ず確かめる
        if (!IsHighestResolutionActive(out string resNow))
        {
            SetNotice($"解像度が {resNow} です。切り替わるまで数秒待ってください");
            return;
        }

        // 【ガード2】CSVロガーと同時に走らせない。
        // 列が包含関係にあるうえ、書き込みが競合して保存が詰まる
        if (_csvLogger != null && _csvLogger.IsRecording)
        {
            SetNotice("CSVロガーが録画中です。左上の Record を先に止めてください");
            return;
        }

        // 【ガード3】撮影の途中で書けなくなるのが最悪なので、先に空きを見る
        long freeBytes = GetFreeBytes();
        if (freeBytes >= 0 && freeBytes < _minFreeMegabytes * 1024L * 1024L)
        {
            SetNotice($"空き容量不足 {(freeBytes / 1024f / 1024f):F0}MB。"
                      + "古い撮影を消してください");
            return;
        }

        SessionName = $"rec_{DateTime.Now:yyyyMMdd_HHmmss}";
        _sessionDir = Path.Combine(Application.persistentDataPath, SessionName);

        try
        {
            Directory.CreateDirectory(_sessionDir);
            _writer = new StreamWriter(
                Path.Combine(_sessionDir, "frames.csv"), false, System.Text.Encoding.UTF8);
            _writer.WriteLine(CsvHeader);
        }
        catch (Exception e)
        {
            SetNotice($"録画を開始できません: {e.Message}");
            Debug.LogException(e);
            _writer = null;
            return;
        }

        _frame = 0;
        SavedCount = 0;
        DroppedCount = 0;
        _lastSavedUnixMs = -1;
        _hasPrevPose = false;
        _stopRequested = false;
        _recordStartTime = Time.realtimeSinceStartup;

        LastMessage = $"録画開始 {SessionName} / {_targetFps}fps";
        Debug.Log(LastMessage);
    }

    private void RequestStop()
    {
        _stopRequested = true;

        // 変換が残っていると最後の数コマの行が落ちる。
        // 残りが片付いてから閉じる
        if (_pending == 0) CloseWriter();
        else LastMessage = $"停止処理中… 残り{_pending}枚";
    }

    private void CloseWriter()
    {
        if (_writer == null) return;

        _writer.Flush();
        _writer.Dispose();
        _writer = null;
        _stopRequested = false;

        LastMessage =
            $"録画停止 {SessionName}\n" +
            $"保存 {SavedCount}枚 / 見送り {DroppedCount}枚";
        Debug.Log(LastMessage);
    }

    // ------------------------------------------------------------
    // 撮影本体
    // ------------------------------------------------------------

    private void OnFrameReceived(ARCameraFrameEventArgs args)
    {
        TrySelectHighestResolutionOnce();

        long unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        bool single = _captureRequested;
        bool stream = IsRecording && IsDue(unixMs);

        if (!single && !stream) return;

        // 変換が追いついていないときは見送る。
        // これは写りの良し悪しの判断ではなく、機械が間に合わなかった分。
        // 枚数を記録してPC側から見えるようにしておく
        if (_pending >= _maxPendingConversions)
        {
            if (stream) DroppedCount++;
            return;
        }

        _captureRequested = false;

        // 姿勢はここで読む。変換のコールバックまで待つと、その時点では
        // カメラがもう動いていて「画像とその瞬間の姿勢」の対応が壊れる
        var t = _arCamera.transform;
        Vector3 position = t.position;
        Quaternion rotation = t.rotation;

        long frameNs = args.timestampNs ?? -1;
        float elapsed = Time.realtimeSinceStartup;

        // 内部パラメータはフレームごとに動く(フォーカスと再校正のため)ので
        // 撮った瞬間の値を控える
        bool hasIntrinsics = _cameraManager.TryGetIntrinsics(out XRCameraIntrinsics k);

        if (!_cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
            LastMessage = "CPU画像を取得できませんでした\n(映像が流れ始めるまで数秒かかります)";
            return;
        }

        // 直前に保存したコマとの角速度。ブレの目安になる。
        // 速いコマを端末側で捨てはしない。記録だけしてPC側で判断する
        string angularSpeed = "";
        if (_hasPrevPose && unixMs > _prevUnixMs)
        {
            float deg = Quaternion.Angle(_prevRotation, rotation);
            float sec = (unixMs - _prevUnixMs) / 1000f;
            LastAngularSpeed = deg / sec;
            angularSpeed = LastAngularSpeed.ToString("F2", CultureInfo.InvariantCulture);
        }
        _prevRotation = rotation;
        _prevUnixMs = unixMs;
        _hasPrevPose = true;

        // 地球座標。追跡できていないときは空欄にする
        var tracking = _earthManager.EarthTrackingState;
        bool geoOk = tracking == TrackingState.Tracking;
        var pose = geoOk ? _earthManager.CameraGeospatialPose : new GeospatialPose();

        int frameIndex = _frame++;
        string fileName = $"frame_{unixMs}.jpg";

        _lastSavedUnixMs = unixMs;
        _pending++;

        // using で必ず返す。返し忘れるとフレームバッファが枯れて映像が止まる。
        // 非同期変換の完了前に Dispose してよいことはAPI仕様で明記されている
        using (image)
        {
            var conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(image.width, image.height),
                outputFormat = TextureFormat.RGBA32,

                // MirrorX = 行の順序を反転(上下反転)。
                // カメラ映像は1行目が画像の上、Texture2Dは1行目が画像の下なので、
                // ここで反転しないとJPEGが上下逆になり、記録した cy と食い違う。
                // ※ MirrorY は左右反転。名前から受ける印象と逆なので注意
                transformation = XRCpuImage.Transformation.MirrorX,
            };

            var row = new RowData
            {
                unixMs = unixMs,
                frameNs = frameNs,
                elapsed = elapsed,
                frameIndex = frameIndex,
                fileName = fileName,
                position = position,
                rotation = rotation,
                tracking = tracking,
                geoOk = geoOk,
                pose = pose,
                hasIntrinsics = hasIntrinsics,
                intrinsics = k,
                angularSpeed = angularSpeed,

                // AR Foundationが返す姿勢は「画面の向き」に合わせたもの。
                // 一方 CPU画像は「センサーの向き」そのまま。端末を縦に持つと
                // 両者は90度ずれる。PC側で戻せるように、向きを生値で残す
                screenOrientation = Screen.orientation,

                note = Note,

                isStreaming = stream,
            };

            image.ConvertAsync(conversionParams,
                (status, p, buffer) => OnConversionComplete(status, p, buffer, row));
        }
    }

    /// <summary>コールバックまで持ち回す、1行分の値</summary>
    private struct RowData
    {
        public long unixMs;
        public long frameNs;
        public float elapsed;
        public int frameIndex;
        public string fileName;
        public Vector3 position;
        public Quaternion rotation;
        public TrackingState tracking;
        public bool geoOk;
        public GeospatialPose pose;
        public bool hasIntrinsics;
        public XRCameraIntrinsics intrinsics;
        public string angularSpeed;
        public ScreenOrientation screenOrientation;
        public string note;
        public bool isStreaming;
    }

    private void OnConversionComplete(
        XRCpuImage.AsyncConversionStatus status,
        XRCpuImage.ConversionParams p,
        NativeArray<byte> buffer,
        RowData row)
    {
        _pending--;

        try
        {
            if (status != XRCpuImage.AsyncConversionStatus.Ready)
            {
                SetNotice($"変換失敗: {status}");
                return;
            }

            int w = p.outputDimensions.x;
            int h = p.outputDimensions.y;

            if (_texture == null || _texture.width != w || _texture.height != h)
            {
                if (_texture != null) Destroy(_texture);
                _texture = new Texture2D(w, h, p.outputFormat, false);
            }

            // buffer はこの呼び出しの間しか有効でない。抜けた瞬間に破棄されるので、
            // 取っておかず、ここで使い切る
            _texture.LoadRawTextureData(buffer);
            _texture.Apply();

            byte[] jpg = _texture.EncodeToJPG(_jpegQuality);

            string dir = row.isStreaming ? _sessionDir : Application.persistentDataPath;
            string path = Path.Combine(dir, row.fileName);

            try
            {
                File.WriteAllBytes(path, jpg);
            }
            catch (Exception e)
            {
                SetNotice($"保存失敗: {e.Message}");
                Debug.LogException(e);
                return;
            }

            if (row.isStreaming && _writer != null)
            {
                WriteRow(row, w, h);
                SavedCount++;

                // ためこみすぎると落ちたときに消える
                if (SavedCount % 60 == 0)
                {
                    _writer.Flush();
                    CheckFreeSpaceDuringRecording();
                }

                LastMessage =
                    $"● {SessionName} {SavedCount}枚 / 見送り{DroppedCount}\n" +
                    $"{w}x{h} {(jpg.Length / 1024f):F0}KB 未処理{_pending}";
            }
            else
            {
                ReportSingleShot(row, w, h, jpg.Length);
            }
        }
        finally
        {
            // 停止待ちの最後の1枚が終わったら閉じる
            if (_stopRequested && _pending == 0) CloseWriter();
        }
    }

    private void WriteRow(RowData row, int imgW, int imgH)
    {
        var c = CultureInfo.InvariantCulture;
        Quaternion q = row.rotation;
        Quaternion eun = row.pose.EunRotation;
        var k = row.intrinsics;

        // 追跡できていない行は空欄にする。ゼロを書くと緯度0度・経度0度という
        // 実在する座標として読まれてしまうため
        string G(bool valid, double v, string f) => valid ? v.ToString(f, c) : "";

        _writer.WriteLine(string.Join(",", new[]
        {
            row.unixMs.ToString(c),
            row.frameNs.ToString(c),
            row.elapsed.ToString("F3", c),
            row.frameIndex.ToString(c),

            ARSession.state.ToString(),
            _earthManager.EarthState.ToString(),
            row.tracking.ToString(),

            row.position.x.ToString("F4", c),
            row.position.y.ToString("F4", c),
            row.position.z.ToString("F4", c),
            q.x.ToString("F6", c), q.y.ToString("F6", c),
            q.z.ToString("F6", c), q.w.ToString("F6", c),

            G(row.geoOk, row.pose.Latitude,  "F8"),
            G(row.geoOk, row.pose.Longitude, "F8"),
            G(row.geoOk, row.pose.Altitude,  "F3"),
            G(row.geoOk, eun.x, "F6"), G(row.geoOk, eun.y, "F6"),
            G(row.geoOk, eun.z, "F6"), G(row.geoOk, eun.w, "F6"),
            G(row.geoOk, row.pose.HorizontalAccuracy,     "F3"),
            G(row.geoOk, row.pose.VerticalAccuracy,       "F3"),
            G(row.geoOk, row.pose.OrientationYawAccuracy, "F3"),

            row.fileName,
            imgW.ToString(c), imgH.ToString(c),
            G(row.hasIntrinsics, k.focalLength.x,    "F3"),
            G(row.hasIntrinsics, k.focalLength.y,    "F3"),
            G(row.hasIntrinsics, k.principalPoint.x, "F3"),
            G(row.hasIntrinsics, k.principalPoint.y, "F3"),

            row.angularSpeed,
            row.screenOrientation.ToString(),
            row.note,
        }));
    }

    private void ReportSingleShot(RowData row, int w, int h, int bytes)
    {
        var c = CultureInfo.InvariantCulture;
        var k = row.intrinsics;

        string intrinsics = row.hasIntrinsics
            ? $"fx={k.focalLength.x.ToString("F1", c)} fy={k.focalLength.y.ToString("F1", c)} " +
              $"cx={k.principalPoint.x.ToString("F1", c)} cy={k.principalPoint.y.ToString("F1", c)}"
            : "内部パラメータ取得不可";

        LastMessage =
            $"保存 {row.fileName}\n" +
            $"{w}x{h} / {(bytes / 1024f).ToString("F0", c)}KB / 品質{_jpegQuality}\n" +
            $"{intrinsics}\n" +
            $"pos {row.position.x.ToString("F3", c)}, {row.position.y.ToString("F3", c)}, " +
            $"{row.position.z.ToString("F3", c)}";

        Debug.Log(LastMessage);
    }

    // ------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------

    private bool IsDue(long unixMs)
    {
        if (_lastSavedUnixMs < 0) return true;
        return unixMs - _lastSavedUnixMs >= 1000L / _targetFps;
    }

    /// <summary>
    /// 起動のたびに640x480へ戻るので、最高解像度を自動で選ぶ。
    /// ただし GetConfigurations() は映像が流れ始めるまで0件を返すため、
    /// 「一覧が取れた最初のタイミング」で1回だけ実行する
    /// </summary>
    private void TrySelectHighestResolutionOnce()
    {
        if (_resolutionSelected || _cameraManager == null) return;

        XRCameraConfiguration? best = null;
        int bestPixels = -1;

        using (var configs = _cameraManager.GetConfigurations(Allocator.Temp))
        {
            if (configs.Length == 0) return;   // まだ一覧が取れない。次のフレームで再挑戦

            foreach (var cfg in configs)
            {
                int pixels = cfg.width * cfg.height;
                if (pixels > bestPixels)
                {
                    bestPixels = pixels;
                    best = cfg;
                }
            }
        }

        _resolutionSelected = true;

        if (!best.HasValue) return;

        var current = _cameraManager.currentConfiguration;
        if (current.HasValue && current.Value == best.Value) return;

        // 切替でカメラが再起動し、トラッキングが一度リセットされる。
        // だから録画開始より前に済ませておく必要がある
        _cameraManager.currentConfiguration = best.Value;
        SetNotice($"解像度を {best.Value.width}x{best.Value.height} に設定しました。"
                  + "トラッキングが一度リセットされます");
    }

    private bool IsHighestResolutionActive(out string currentText)
    {
        currentText = "不明";
        if (_cameraManager == null) return false;

        var current = _cameraManager.currentConfiguration;
        if (!current.HasValue) return false;

        currentText = $"{current.Value.width}x{current.Value.height}";

        using (var configs = _cameraManager.GetConfigurations(Allocator.Temp))
        {
            int currentPixels = current.Value.width * current.Value.height;
            foreach (var cfg in configs)
            {
                if (cfg.width * cfg.height > currentPixels) return false;
            }
        }

        return true;
    }

    private void CheckFreeSpaceDuringRecording()
    {
        long freeBytes = GetFreeBytes();
        if (freeBytes < 0) return;

        if (freeBytes < _minFreeMegabytes * 1024L * 1024L)
        {
            Debug.LogWarning("空き容量不足のため録画を自動停止します");
            RequestStop();
            SetNotice($"空き容量不足で自動停止 ({(freeBytes / 1024f / 1024f):F0}MB)。古い撮影を消してください");
        }
    }

    private long GetFreeBytes()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var statFs = new AndroidJavaObject(
                       "android.os.StatFs", Application.persistentDataPath))
            {
                return statFs.Call<long>("getAvailableBytes");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"空き容量を取得できません: {e.Message}");
            return -1;
        }
#else
        return -1;
#endif
    }
}
