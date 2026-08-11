using UnityEngine;

/// <summary>
/// 画面の色づかいを1か所にまとめたもの。
///
/// MonoBehaviour ではなく [Serializable] なので、これを持っている
/// GeospatialStatusDisplay の Inspector に、そのまま折りたたみで出る。
///
/// 案内の色はこの道具の性格そのもの（落ち着いた測量道具にも、
/// 明るい撮影アプリにもなる）なので、コードに埋めずここで触れるようにした。
/// </summary>
[System.Serializable]
public class CaptureTheme
{
    [Header("見出しの色（状態ごと）")]
    [Tooltip("撮影できます")]
    public Color ready = new Color32(0x5B, 0xE5, 0x8A, 0xFF);

    [Tooltip("録画中")]
    public Color recording = new Color32(0xFF, 0x5A, 0x5A, 0xFF);

    [Tooltip("撮れるが精度が不十分／位置が止まっている")]
    public Color caution = new Color32(0xFF, 0xC2, 0x4B, 0xFF);

    [Tooltip("準備中／方位を調整中／位置を取得中")]
    public Color wait = new Color32(0x8F, 0xC2, 0xFF, 0xFF);

    [Tooltip("撮れない（解像度・空き容量・接続不足）")]
    public Color blocked = new Color32(0xFF, 0x7A, 0x5A, 0xFF);

    [Header("本文")]
    [Tooltip("見出しの下の案内文")]
    public Color advice = new Color32(0xDD, 0xDD, 0xDD, 0xFF);

    [Tooltip("認識中の目印の数")]
    public Color pointCount = new Color32(0x9F, 0xBE, 0xDE, 0xFF);

    [Tooltip("外挿の警告など")]
    public Color warning = new Color32(0xFF, 0xC2, 0x4B, 0xFF);

    [Header("チェック項目（i ボタンの中）")]
    public Color label = new Color32(0xCC, 0xCC, 0xCC, 0xFF);
    public Color value = new Color32(0xFF, 0xFF, 0xFF, 0xFF);
    public Color noteValue = new Color32(0xFF, 0xC2, 0x4B, 0xFF);
    public Color detail = new Color32(0x99, 0x99, 0x99, 0xFF);

    [Header("チェック印")]
    public Color markOk = new Color32(0x4C, 0xDE, 0x6A, 0xFF);
    public Color markWarn = new Color32(0xFF, 0xC2, 0x4B, 0xFF);
    public Color markBad = new Color32(0xFF, 0x5A, 0x5A, 0xFF);

    [Header("文字の大きさ（%）")]
    [Range(100, 250)] public int headlineSize = 170;
    [Range(100, 200)] public int recordingSize = 140;
    [Range(60, 130)] public int adviceSize = 95;
    [Range(50, 120)] public int pointCountSize = 80;
    [Range(50, 120)] public int detailSize = 80;

    /// <summary>状態に対応する見出しの色</summary>
    public Color HeadlineColor(CaptureGuidance.Level level)
    {
        switch (level)
        {
            case CaptureGuidance.Level.Ready: return ready;
            case CaptureGuidance.Level.Recording: return recording;
            case CaptureGuidance.Level.Caution: return caution;
            case CaptureGuidance.Level.Wait: return wait;
            default: return blocked;
        }
    }

    /// <summary>チェック印。2=良好 1=注意 0=だめ</summary>
    public string Mark(int state)
    {
        switch (state)
        {
            case 2: return Wrap("○", markOk);
            case 1: return Wrap("△", markWarn);
            default: return Wrap("×", markBad);
        }
    }

    /// <summary>色付きのリッチテキストにする</summary>
    public static string Wrap(string text, Color c)
    {
        return $"<color=#{ColorUtility.ToHtmlStringRGBA(c)}>{text}</color>";
    }
}
