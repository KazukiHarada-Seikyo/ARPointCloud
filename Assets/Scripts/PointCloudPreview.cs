using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// ARCoreの特徴点を、カメラ映像の上に光の粒として重ねる。
///
/// --------------------------------------------------------------------
/// これは「完成する点群」ではない
///
/// ARCoreが自己位置推定のために常時計算している特徴点をそのまま出している。
/// 数百〜数千点で色も無い。PC側で作る点群(実測3万点)とは別物。
///
/// 目的は「どこを覆えたか」を見せること。**これがあなたの点群です、
/// という見せ方をしてはいけない。** 嘘になる。
/// --------------------------------------------------------------------
///
/// 演出の分担
///
///   スクリプト … 点をためる / 波を出す時刻を決める
///   シェーダ   … 時間で変わるもの全部(現れる動き・またたき・波)
///
/// 時間の関数をGPUに寄せてあるので、**メッシュを作り直すのは点が
/// 増減したときだけ**。以前は演出中ずっと0.1秒ごとに作り直していた。
/// 撮影中は端末が熱を持って1フレームの余裕が無くなるので、ここが効く。
///
/// 描画はスクリプトだけで完結する。実行時に子オブジェクトとマテリアルを
/// 作るので、シーンにプレハブを置く必要はない。
/// </summary>
[RequireComponent(typeof(Transform))]
public class PointCloudPreview : MonoBehaviour
{
    [SerializeField] private ARPointCloudManager _pointCloudManager;

    [Tooltip("録画開始で表示をまっさらにするために見ている。未設定でも動く")]
    [SerializeField] private FrameCapture _capture;

    [Tooltip("波の出どころ。未設定なら Camera.main を使う")]
    [SerializeField] private Transform _waveOrigin;

    [Header("見た目")]
    [SerializeField, Range(2f, 40f)] private float _pointSize = 7f;

    [Tooltip("現れた直後の色")]
    [SerializeField] private Color _freshColor = new Color(1f, 1f, 1f, 0.95f);

    [Tooltip("落ち着いたあとの色")]
    [SerializeField] private Color _settledColor = new Color(0.62f, 0.82f, 1f, 0.42f);

    [Tooltip("現れてから落ち着くまでの秒数")]
    [SerializeField] private float _settleSeconds = 0.7f;

    [Tooltip("現れた瞬間の大きさの倍率。大きくすると賑やかになる")]
    [SerializeField] private float _popScale = 1.5f;

    [Header("ネオン")]
    [Tooltip("芯を加算で光らせる強さ。0で以前と同じ落ち着いた見た目、" +
             "上げるほどネオンらしくなる。屋外で白飛びするようなら下げる")]
    [SerializeField, Range(0f, 1f)] private float _glow = 0.55f;

    [Tooltip("光のにじみの広さ")]
    [SerializeField, Range(0f, 1f)] private float _haloSize = 0.6f;

    [Header("またたき")]
    [Tooltip("星の瞬きの深さ。0で止まる")]
    [SerializeField, Range(0f, 1f)] private float _twinkleAmount = 0.35f;

    [SerializeField, Range(0f, 12f)] private float _twinkleSpeed = 3f;

    [Header("スキャンウェーブ")]
    [Tooltip("波を出す。カメラの位置から輪が広がり、通ったところの粒が光る")]
    [SerializeField] private bool _waveEnabled = true;

    [Tooltip("波を出す間隔(秒)")]
    [SerializeField, Range(0.3f, 8f)] private float _waveInterval = 1.6f;

    [Tooltip("この距離(m)動いたら、間隔を待たずに次の波を出す。" +
             "「カメラを動かすと波が出る」ようにするためのもの")]
    [SerializeField, Range(0f, 3f)] private float _waveMoveTrigger = 0.7f;

    [SerializeField, Range(0.5f, 20f)] private float _waveSpeed = 4f;
    [SerializeField, Range(0.05f, 3f)] private float _waveWidth = 0.55f;
    [SerializeField, Range(0f, 3f)] private float _waveStrength = 1.2f;

    [Tooltip("波が届く距離(m)。ここまで来たら消える")]
    [SerializeField, Range(1f, 40f)] private float _waveRange = 12f;

    [Header("重複の除き方")]
    [Tooltip("この間隔(m)の格子で同じ点とみなす。" +
             "ARCoreは同じ物理点に別の識別子を振り直すことがあるため、" +
             "識別子ではなく位置で判断する")]
    [SerializeField] private float _cellSize = 0.03f;

    [Header("負荷")]
    [Tooltip("持つ点の上限。古いものから捨てる")]
    [SerializeField] private int _maxPoints = 12000;

    [Tooltip("メッシュを作り直す間隔(秒)。点が増えていなければ作り直さない")]
    [SerializeField] private float _rebuildInterval = 0.1f;

    [Tooltip("撮影中は演出を控えめにする。" +
             "波を止め、またたきを浅くする。発熱で保存が追いつかなくなるのを" +
             "避けるための保険。見た目を優先するなら外してよい")]
    [SerializeField] private bool _calmWhileRecording = true;

    /// <summary>いま表示している点の数。画面に出す</summary>
    public int PointCount => _points.Count;

    private struct Entry
    {
        public Vector3 world;
        public float bornAt;
        public float seed;      // またたきの位相をずらすための乱数 0〜1
    }

    // 空間を格子に切り、同じマスに落ちた点は同じ点とみなす。
    //
    // 当初は ARCore の識別子で重複を除いていたが、ARCore は同じ物理的な点に
    // 別の識別子を振り直すことがある。すると毎回「新しい点」と判定されて
    // 光り直すので、画面が激しく点滅した。位置で判断すれば起きない。
    private readonly Dictionary<long, Entry> _points = new Dictionary<long, Entry>();

    // 上限を超えたとき古いものから捨てる。List だと先頭削除で全体がずれるので
    // (12000点だと毎回12000要素の移動)、Queue にして O(1) にする
    private readonly Queue<long> _order = new Queue<long>();

    // 中身が変わっていなければメッシュを作り直さない。
    // 演出は時間の関数としてGPU側で解くので、ここは点の増減だけを見ればよい
    private bool _dirty;

    private Mesh _mesh;
    private Material _material;
    private float _nextRebuild;
    private bool _wasRecording;

    // 波は2本まで重ねる。1本だと間隔の切れ目で途切れて見える
    private Vector4 _wave0;
    private Vector4 _wave1;
    private int _nextWaveSlot;
    private float _nextWaveAt;
    private float _lastWaveAt = -999f;
    private Vector3 _lastWavePos;
    private bool _hasWavePos;

    // メッシュ組み立て用。使い回してGCを抑える
    private Vector3[] _vertices;
    private Vector2[] _uvs;
    private Vector2[] _uv2s;
    private Color[] _colors;
    private int[] _indices;
    private int _capacity;

    private static readonly Vector2[] Corners =
    {
        new Vector2(-1f, -1f), new Vector2(1f, -1f),
        new Vector2(-1f, 1f), new Vector2(1f, 1f),
    };

    private static readonly int IdPointSize = Shader.PropertyToID("_PointSize");
    private static readonly int IdGlow = Shader.PropertyToID("_Glow");
    private static readonly int IdHaloSize = Shader.PropertyToID("_HaloSize");
    private static readonly int IdTwinkleAmount = Shader.PropertyToID("_TwinkleAmount");
    private static readonly int IdTwinkleSpeed = Shader.PropertyToID("_TwinkleSpeed");
    private static readonly int IdFreshColor = Shader.PropertyToID("_FreshColor");
    private static readonly int IdSettledColor = Shader.PropertyToID("_SettledColor");
    private static readonly int IdSettleSeconds = Shader.PropertyToID("_SettleSeconds");
    private static readonly int IdPopScale = Shader.PropertyToID("_PopScale");
    private static readonly int IdWave0 = Shader.PropertyToID("_Wave0");
    private static readonly int IdWave1 = Shader.PropertyToID("_Wave1");
    private static readonly int IdWaveSpeed = Shader.PropertyToID("_WaveSpeed");
    private static readonly int IdWaveWidth = Shader.PropertyToID("_WaveWidth");
    private static readonly int IdWaveStrength = Shader.PropertyToID("_WaveStrength");
    private static readonly int IdWaveRange = Shader.PropertyToID("_WaveRange");

    private GameObject _rendererObject;

    // ------------------------------------------------------------

    private void Awake()
    {
        var shader = Shader.Find("ARPointCloud/PointPreview");
        if (shader == null)
        {
            Debug.LogError(
                "シェーダ ARPointCloud/PointPreview が見つかりません。\n" +
                "Project Settings → Graphics → Always Included Shaders に" +
                "登録されているか確認してください");
            enabled = false;
            return;
        }

        _material = new Material(shader) { hideFlags = HideFlags.DontSave };
        PushSettings();

        _mesh = new Mesh { name = "PointCloudPreview", indexFormat = IndexFormat.UInt32 };
        _mesh.MarkDynamic();

        // 頂点を世界座標のまま入れるので、描画側の変換は単位行列にしておく
        var go = new GameObject("PointCloudPreviewRenderer");
        go.transform.SetParent(null);
        go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        go.transform.localScale = Vector3.one;
        go.hideFlags = HideFlags.DontSave;

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = _mesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = _material;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off;

        _rendererObject = go;
    }

    private void OnEnable()
    {
        if (_pointCloudManager != null)
            _pointCloudManager.trackablesChanged.AddListener(OnPointCloudsChanged);
    }

    private void OnDisable()
    {
        if (_pointCloudManager != null)
            _pointCloudManager.trackablesChanged.RemoveListener(OnPointCloudsChanged);
    }

    private void OnDestroy()
    {
        if (_rendererObject != null) Destroy(_rendererObject);
        if (_mesh != null) Destroy(_mesh);
        if (_material != null) Destroy(_material);
    }

#if UNITY_EDITOR
    // Inspector を触ったら、実行中でもすぐ絵に出す
    private void OnValidate()
    {
        if (Application.isPlaying && _material != null) PushSettings();
    }
#endif

    /// <summary>Inspector の値をマテリアルへ渡す</summary>
    private void PushSettings()
    {
        bool calm = _calmWhileRecording && _capture != null && _capture.IsRecording;

        _material.SetFloat(IdPointSize, _pointSize);
        _material.SetFloat(IdGlow, calm ? _glow * 0.5f : _glow);
        _material.SetFloat(IdHaloSize, _haloSize);
        _material.SetFloat(IdTwinkleAmount, calm ? _twinkleAmount * 0.3f : _twinkleAmount);
        _material.SetFloat(IdTwinkleSpeed, _twinkleSpeed);
        _material.SetColor(IdFreshColor, _freshColor);
        _material.SetColor(IdSettledColor, _settledColor);
        _material.SetFloat(IdSettleSeconds, Mathf.Max(0.01f, _settleSeconds));
        _material.SetFloat(IdPopScale, _popScale);
        _material.SetFloat(IdWaveSpeed, _waveSpeed);
        _material.SetFloat(IdWaveWidth, _waveWidth);
        _material.SetFloat(IdWaveStrength, _waveStrength);
        _material.SetFloat(IdWaveRange, _waveRange);
    }

    /// <summary>表示をまっさらにする。ボタンからも呼べる</summary>
    public void Clear()
    {
        _points.Clear();
        _order.Clear();
        _dirty = true;
        _wave0 = Vector4.zero;
        _wave1 = Vector4.zero;
        _hasWavePos = false;
        _lastWaveAt = -999f;
        if (_mesh != null) _mesh.Clear();
        if (_material != null)
        {
            _material.SetVector(IdWave0, _wave0);
            _material.SetVector(IdWave1, _wave1);
        }
    }

    // ------------------------------------------------------------
    // 点をためる
    // ------------------------------------------------------------

    private void OnPointCloudsChanged(
        ARTrackablesChangedEventArgs<ARPointCloud> args)
    {
        foreach (var cloud in args.added) Accumulate(cloud);
        foreach (var cloud in args.updated) Accumulate(cloud);
    }

    private void Accumulate(ARPointCloud cloud)
    {
        if (cloud.positions == null) return;

        NativeSlice<Vector3> positions = cloud.positions.Value;

        // 位置は点群空間なので、世界座標へ移す
        Matrix4x4 toWorld = cloud.transform.localToWorldMatrix;
        float now = Time.time;
        float inv = 1f / Mathf.Max(0.001f, _cellSize);

        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 world = toWorld.MultiplyPoint3x4(positions[i]);
            long key = CellKey(world, inv);

            if (_points.TryGetValue(key, out Entry e))
            {
                // 同じマスに落ちた＝同じ点。現れた時刻は保つ。
                // ここで bornAt を更新すると、また光り直して点滅する
                e.world = world;
                _points[key] = e;
            }
            else
            {
                _points[key] = new Entry
                {
                    world = world,
                    bornAt = now,
                    // 位置から決める。毎回 Random を引くとフレームで絵が変わる
                    seed = Frac(world.x * 12.9898f + world.y * 78.233f
                                + world.z * 37.719f),
                };
                _order.Enqueue(key);
                _dirty = true;
            }
        }

        // 上限を超えたら古いものから捨てる
        while (_order.Count > _maxPoints)
        {
            _points.Remove(_order.Dequeue());
            _dirty = true;
        }
    }

    /// <summary>カメラの Transform。毎フレーム Camera.main を引かないよう覚えておく</summary>
    private Transform CachedCamera()
    {
        if (_cameraTransform != null) return _cameraTransform;

        // シーンの読み込み途中はまだ居ないことがあるので、見つかるまで毎フレーム探す。
        // 見つかればそれ以降は引かない
        Camera cam = Camera.main;
        if (cam != null) _cameraTransform = cam.transform;
        return _cameraTransform;
    }

    private Transform _cameraTransform;

    private static float Frac(float x)
    {
        x = Mathf.Sin(x) * 43758.5453f;
        return x - Mathf.Floor(x);
    }

    /// <summary>世界座標を格子のマス番号にして、1つの整数に詰める</summary>
    private static long CellKey(Vector3 p, float inv)
    {
        // ±10万マス(既定3cmなら±3km)まで衝突しない
        long x = (long)Mathf.Floor(p.x * inv) + 0x40000;
        long y = (long)Mathf.Floor(p.y * inv) + 0x40000;
        long z = (long)Mathf.Floor(p.z * inv) + 0x40000;
        return (x << 42) ^ (y << 21) ^ z;
    }

    // ------------------------------------------------------------
    // 描く
    // ------------------------------------------------------------

    private void Update()
    {
        // 録画を始めたら、前回の点を引きずらないよう消す
        bool recording = _capture != null && _capture.IsRecording;
        if (recording != _wasRecording)
        {
            if (recording) Clear();
            PushSettings();            // 控えめ設定の切り替え
        }
        _wasRecording = recording;

        UpdateWaves(recording);

        if (Time.time < _nextRebuild) return;
        _nextRebuild = Time.time + _rebuildInterval;

        // 点が増減していなければ作り直さない。
        // 現れる動き・またたき・波はすべてGPU側で時間から解いているので、
        // 見た目が止まることはない
        if (!_dirty) return;
        _dirty = false;

        Rebuild();
    }

    /// <summary>波を出す時刻を決めて、マテリアルへ渡す</summary>
    private void UpdateWaves(bool recording)
    {
        if (_material == null) return;

        bool calm = _calmWhileRecording && recording;
        if (!_waveEnabled || calm)
        {
            // 出ている波は最後まで走らせて、新しい波を出さないだけにする。
            // 途中で消すと不自然に途切れる
            _material.SetVector(IdWave0, _wave0);
            _material.SetVector(IdWave1, _wave1);
            return;
        }

        Transform src = _waveOrigin != null ? _waveOrigin : CachedCamera();
        if (src == null) return;

        Vector3 pos = src.position;
        float now = Time.time;

        // 歩いていると「動いたら出す」が何度も成立する。そのまま出すと
        // 2本しか持てない枠を上書きし続けて、どの波も途中で消える。
        // 間隔の半分は空けて、1本あたりの寿命を確保する
        bool moved = _hasWavePos
                     && _waveMoveTrigger > 0f
                     && now >= _lastWaveAt + _waveInterval * 0.5f
                     && (pos - _lastWavePos).sqrMagnitude
                        > _waveMoveTrigger * _waveMoveTrigger;

        if (now >= _nextWaveAt || moved || !_hasWavePos)
        {
            // w は「出た時刻」だが、0 は『まだ出ていない』印に使っている。
            // 起動直後の1フレーム目は Time.time が 0 になりうるので避ける
            float emitAt = Mathf.Max(now, 0.001f);

            var wave = new Vector4(pos.x, pos.y, pos.z, emitAt);
            if (_nextWaveSlot == 0) _wave0 = wave;
            else _wave1 = wave;
            _nextWaveSlot ^= 1;

            _nextWaveAt = now + _waveInterval;
            _lastWaveAt = now;
            _lastWavePos = pos;
            _hasWavePos = true;
        }

        _material.SetVector(IdWave0, _wave0);
        _material.SetVector(IdWave1, _wave1);
    }

    private void Rebuild()
    {
        int count = _order.Count;
        if (count == 0)
        {
            _mesh.Clear();
            return;
        }

        EnsureCapacity(count);

        Vector3 min = Vector3.positiveInfinity;
        Vector3 max = Vector3.negativeInfinity;

        int v = 0;
        int t = 0;
        foreach (long key in _order)
        {
            if (!_points.TryGetValue(key, out Entry e)) continue;

            // 時間で変わるものはここでは触らない。
            // 生まれた時刻と乱数だけ渡して、あとはシェーダに任せる
            var born = new Vector2(e.bornAt, e.seed);

            for (int k = 0; k < 4; k++)
            {
                _vertices[v + k] = e.world;
                _uvs[v + k] = Corners[k];
                _uv2s[v + k] = born;
                _colors[v + k] = Color.white;
            }

            _indices[t++] = v;
            _indices[t++] = v + 2;
            _indices[t++] = v + 1;
            _indices[t++] = v + 1;
            _indices[t++] = v + 2;
            _indices[t++] = v + 3;

            v += 4;

            min = Vector3.Min(min, e.world);
            max = Vector3.Max(max, e.world);
        }

        // 残りは潰しておく(配列を使い回しているため)
        for (int i = v; i < _capacity * 4; i++)
        {
            _vertices[i] = Vector3.zero;
            _colors[i] = Color.clear;
            _uv2s[i] = Vector2.zero;
        }
        for (int i = t; i < _capacity * 6; i++) _indices[i] = 0;

        _mesh.Clear();
        _mesh.vertices = _vertices;
        _mesh.uv = _uvs;
        _mesh.uv2 = _uv2s;
        _mesh.colors = _colors;
        _mesh.SetIndices(_indices, MeshTopology.Triangles, 0, false);

        // 世界座標のまま入れているので、境界は自分で決める。
        // 間違えるとカメラの外と判定されて丸ごと消える。
        // 演出で粒が膨らむぶん、少し広めに取る
        if (v > 0)
        {
            var b = new Bounds();
            b.SetMinMax(min - Vector3.one * 0.5f, max + Vector3.one * 0.5f);
            _mesh.bounds = b;
        }
    }

    private void EnsureCapacity(int points)
    {
        if (points <= _capacity) return;

        _capacity = Mathf.NextPowerOfTwo(Mathf.Max(points, 256));
        _vertices = new Vector3[_capacity * 4];
        _uvs = new Vector2[_capacity * 4];
        _uv2s = new Vector2[_capacity * 4];
        _colors = new Color[_capacity * 4];
        _indices = new int[_capacity * 6];
    }
}
