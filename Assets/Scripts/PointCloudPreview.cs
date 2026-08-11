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
/// 描画はスクリプトだけで完結する。実行時に子オブジェクトとマテリアルを
/// 作るので、シーンにプレハブを置く必要はない。
/// </summary>
[RequireComponent(typeof(Transform))]
public class PointCloudPreview : MonoBehaviour
{
    [SerializeField] private ARPointCloudManager _pointCloudManager;

    [Tooltip("録画開始で表示をまっさらにするために見ている。未設定でも動く")]
    [SerializeField] private FrameCapture _capture;

    [Header("見た目")]
    [SerializeField, Range(2f, 40f)] private float _pointSize = 9f;

    [Tooltip("現れた直後の色")]
    [SerializeField] private Color _freshColor = new Color(0.42f, 0.95f, 0.62f);

    [Tooltip("落ち着いたあとの色")]
    [SerializeField] private Color _settledColor = new Color(0.55f, 0.75f, 1f, 0.55f);

    [Tooltip("現れてから落ち着くまでの秒数")]
    [SerializeField] private float _settleSeconds = 1.2f;

    [Tooltip("現れた瞬間の大きさの倍率")]
    [SerializeField] private float _popScale = 2.2f;

    [Header("負荷")]
    [Tooltip("持つ点の上限。古いものから捨てる")]
    [SerializeField] private int _maxPoints = 12000;

    [Tooltip("メッシュを作り直す間隔(秒)。毎フレームは要らない")]
    [SerializeField] private float _rebuildInterval = 0.1f;

    /// <summary>いま表示している点の数。画面に出す</summary>
    public int PointCount => _points.Count;

    private struct Entry
    {
        public Vector3 world;
        public float bornAt;
    }

    // 同じ点が毎フレーム届くので、識別子で重複を除く
    private readonly Dictionary<ulong, Entry> _points = new Dictionary<ulong, Entry>();
    private readonly List<ulong> _order = new List<ulong>();

    private Mesh _mesh;
    private Material _material;
    private float _nextRebuild;
    private bool _wasRecording;

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
        _material.SetFloat("_PointSize", _pointSize);

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

    private GameObject _rendererObject;

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

    /// <summary>表示をまっさらにする。ボタンからも呼べる</summary>
    public void Clear()
    {
        _points.Clear();
        _order.Clear();
        if (_mesh != null) _mesh.Clear();
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
        if (cloud.positions == null || cloud.identifiers == null) return;

        NativeSlice<Vector3> positions = cloud.positions.Value;
        NativeSlice<ulong> ids = cloud.identifiers.Value;
        int n = Mathf.Min(positions.Length, ids.Length);

        // 位置は点群空間なので、世界座標へ移す
        Matrix4x4 toWorld = cloud.transform.localToWorldMatrix;
        float now = Time.time;

        for (int i = 0; i < n; i++)
        {
            ulong id = ids[i];
            Vector3 world = toWorld.MultiplyPoint3x4(positions[i]);

            if (_points.TryGetValue(id, out Entry e))
            {
                // 既にある点は位置だけ直す。現れた時刻は保つ
                e.world = world;
                _points[id] = e;
            }
            else
            {
                _points[id] = new Entry { world = world, bornAt = now };
                _order.Add(id);
            }
        }

        // 上限を超えたら古いものから捨てる
        while (_order.Count > _maxPoints)
        {
            _points.Remove(_order[0]);
            _order.RemoveAt(0);
        }
    }

    // ------------------------------------------------------------
    // 描く
    // ------------------------------------------------------------

    private void Update()
    {
        // 録画を始めたら、前回の点を引きずらないよう消す
        bool recording = _capture != null && _capture.IsRecording;
        if (recording && !_wasRecording) Clear();
        _wasRecording = recording;

        if (Time.time < _nextRebuild) return;
        _nextRebuild = Time.time + _rebuildInterval;

        Rebuild();
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

        float now = Time.time;
        Vector3 min = Vector3.positiveInfinity;
        Vector3 max = Vector3.negativeInfinity;

        int v = 0;
        int t = 0;
        for (int i = 0; i < count; i++)
        {
            if (!_points.TryGetValue(_order[i], out Entry e)) continue;

            // 0 = 現れた瞬間, 1 = 落ち着いた
            float age = Mathf.Clamp01((now - e.bornAt) / _settleSeconds);
            float ease = age * age * (3f - 2f * age);   // なめらかに

            Color c = Color.Lerp(_freshColor, _settledColor, ease);
            float scale = Mathf.Lerp(_popScale, 1f, ease);

            for (int k = 0; k < 4; k++)
            {
                _vertices[v + k] = e.world;
                _uvs[v + k] = Corners[k];
                _uv2s[v + k] = new Vector2(scale, 0f);
                _colors[v + k] = c;
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
        }
        for (int i = t; i < _capacity * 6; i++) _indices[i] = 0;

        _mesh.Clear();
        _mesh.vertices = _vertices;
        _mesh.uv = _uvs;
        _mesh.uv2 = _uv2s;
        _mesh.colors = _colors;
        _mesh.SetIndices(_indices, MeshTopology.Triangles, 0, false);

        // 世界座標のまま入れているので、境界は自分で決める。
        // 間違えるとカメラの外と判定されて丸ごと消える
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
