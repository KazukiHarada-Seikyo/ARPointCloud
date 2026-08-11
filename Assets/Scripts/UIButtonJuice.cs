using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// ボタンに手触りを足す。押すと縮み、離すとぷるんと戻る。
///
/// --------------------------------------------------------------------
/// なぜ曲線ではなくバネなのか
///
/// CSSの transition のように「決まった曲線を決まった時間で辿る」やり方だと、
/// 連打したときに途中から不自然に飛ぶ。
///
/// ここではバネの運動方程式をそのまま解いている。
///     加速度 = (目標 - 現在) * かたさ - 速度 * 減衰
/// 途中でどこにいても、そこから自然に続く。連打しても破綻しない。
///
/// 減衰を「かたさの平方根 × 2」より小さくすると行き過ぎ（overshoot）が出る。
/// これが「ぷるん」の正体。既定値はわざと弱めの減衰にしてある。
/// --------------------------------------------------------------------
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class UIButtonJuice : MonoBehaviour,
    IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
{
    [Header("押し込み")]
    [Tooltip("押している間の大きさ")]
    [SerializeField, Range(0.5f, 1f)] private float _pressedScale = 0.88f;

    [Header("バネ")]
    [Tooltip("かたさ。大きいほど速く戻る")]
    [SerializeField] private float _stiffness = 620f;

    [Tooltip("減衰。小さいほどよく跳ねる。0にすると永久に揺れる")]
    [SerializeField] private float _damping = 22f;

    [Header("離したときの弾み")]
    [Tooltip("指を離した瞬間に加える勢い。0で弾まない")]
    [SerializeField] private float _releaseKick = 2.4f;

    [Header("ひねり")]
    [Tooltip("押したときに傾ける角度。0で傾かない。3〜8度で愛嬌が出る")]
    [SerializeField] private float _tiltDegrees;

    [Tooltip("傾く向きを毎回変える。同じ動きの繰り返しに見えなくなる")]
    [SerializeField] private bool _randomTiltDirection = true;

    private RectTransform _rect;
    private float _scale = 1f;
    private float _scaleVelocity;
    private float _scaleTarget = 1f;

    private float _tilt;
    private float _tiltVelocity;
    private float _tiltTarget;
    private float _tiltSign = 1f;

    private void Awake()
    {
        _rect = GetComponent<RectTransform>();
    }

    private void OnDisable()
    {
        _scale = _scaleTarget = 1f;
        _scaleVelocity = 0f;
        _tilt = _tiltTarget = _tiltVelocity = 0f;
        Apply();
    }

    private void Update()
    {
        // 端末の描画が重くても挙動が変わらないよう、刻みを小さく切って解く。
        // まとめて1回で解くと、重い瞬間にバネが暴れる
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        int steps = 4;
        float h = dt / steps;

        for (int i = 0; i < steps; i++)
        {
            Step(ref _scale, ref _scaleVelocity, _scaleTarget, h);
            Step(ref _tilt, ref _tiltVelocity, _tiltTarget, h);
        }

        Apply();
    }

    private void Step(ref float value, ref float velocity, float target, float h)
    {
        float accel = (target - value) * _stiffness - velocity * _damping;
        velocity += accel * h;
        value += velocity * h;
    }

    private void Apply()
    {
        if (_rect == null) return;
        _rect.localScale = new Vector3(_scale, _scale, 1f);
        if (_tiltDegrees != 0f) _rect.localRotation = Quaternion.Euler(0f, 0f, _tilt);
    }

    // ------------------------------------------------------------

    public void OnPointerDown(PointerEventData eventData)
    {
        _scaleTarget = _pressedScale;

        if (_tiltDegrees != 0f)
        {
            if (_randomTiltDirection) _tiltSign = Random.value < 0.5f ? -1f : 1f;
            _tiltTarget = _tiltDegrees * _tiltSign;
        }
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        _scaleTarget = 1f;
        _tiltTarget = 0f;

        // 離した瞬間に外向きの勢いを加える。これが無いとただ戻るだけで、
        // 「弾んだ」感じにならない
        _scaleVelocity += _releaseKick;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        // クリック成立時は何もしない。押下と解放でもう十分動いている。
        // ここで足すと連打したときに過剰になる
    }

    /// <summary>外から弾ませる。メモが切り替わった瞬間などに使う</summary>
    public void Kick(float amount = 3.2f)
    {
        _scaleVelocity += amount;
    }

    /// <summary>外からひねる。角度は度</summary>
    public void KickTilt(float degrees)
    {
        _tiltVelocity += degrees;
    }
}
