using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// 撮影画面のUIを、UI_SETUP_V2.md のとおりに揃える。
///
/// 手作業だと「Image を外して UICircle を足す」を4か所、OnClick を5か所、
/// 参照を5か所と、取りこぼしが起きやすい。ここで一括してやる。
///
/// メニュー: Tools → ARPointCloud → 撮影画面のUIを仕上げる
///
/// 何度実行してもよい（すでに正しいものは触らない）。
/// </summary>
public static class CaptureUiFixer
{
    private const string MenuPath = "Tools/ARPointCloud/撮影画面のUIを仕上げる";

    // UI_SETUP_V2.md の値
    private static readonly Color SideButton = new Color(1f, 1f, 1f, 0.18f);
    private static readonly Color RecordRing = new Color(1f, 1f, 1f, 0.95f);
    private static readonly Color RecordInner = new Color32(0xEF, 0x4B, 0x45, 0xFF);

    [MenuItem(MenuPath)]
    public static void Fix()
    {
        var log = new System.Text.StringBuilder();
        int changed = 0;

        // --- 1. 壊れたコンポーネントを外す -----------------------------
        foreach (var t in AllTransforms())
        {
            int n = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            if (n > 0)
            {
                log.AppendLine($"  壊れたコンポーネントを {n} 個外した: {t.name}");
                changed += n;
            }
        }

        // --- 2. Image を UICircle に置き換える -------------------------
        changed += ReplaceWithCircle("MemoButton", SideButton, 1f, 0f, true, log);
        changed += ReplaceWithCircle("CSVButton", SideButton, 1f, 0f, true, log);
        changed += ReplaceWithCircle("FrameRecord", RecordRing, 1f, 0.055f, true, log);
        changed += ReplaceWithCircle("Inner", RecordInner, 1f, 0f, false, log);

        // --- 3. Button の Target Graphic を直す ------------------------
        changed += FixTargetGraphic("MemoButton", log);
        changed += FixTargetGraphic("CSVButton", log);
        changed += FixTargetGraphic("FrameRecord", log);

        // --- 4. 録画ボタンの文字を消す ---------------------------------
        var recText = Find("FrameRecord")?.GetComponentInChildren<TextMeshProUGUI>();
        if (recText != null && !string.IsNullOrEmpty(recText.text))
        {
            Undo.RecordObject(recText, "clear label");
            recText.text = "";
            EditorUtility.SetDirty(recText);
            log.AppendLine("  録画ボタンの文字を消した");
            changed++;
        }

        // --- 5. OnClick を直す -----------------------------------------
        var capture = Object.FindAnyObjectByType<FrameCapture>();
        var display = Object.FindAnyObjectByType<GeospatialStatusDisplay>();
        var logger = Object.FindAnyObjectByType<GeospatialCsvLogger>();

        if (capture != null)
        {
            changed += SetOnClick("DeleteButton",
                new UnityAction(capture.DeleteOldestRecording), log);
            changed += SetOnClick("MemoButton",
                new UnityAction(capture.CycleNote), log);
            changed += SetOnClick("FrameRecord",
                new UnityAction(capture.ToggleRecording), log);
        }
        if (display != null)
            changed += SetOnClick("CSVButton",
                new UnityAction(display.ToggleDetail), log);
        if (logger != null)
            changed += SetOnClick("Record",
                new UnityAction(logger.ToggleRecording), log);

        // --- 6. CaptureButtons の参照 ----------------------------------
        var buttons = Object.FindAnyObjectByType<CaptureButtons>();
        var inner = Find("Inner");
        if (buttons != null && inner != null)
        {
            var so = new SerializedObject(buttons);
            changed += SetRef(so, "_capture", capture, log);
            changed += SetRef(so, "_recordInner", inner.GetComponent<UICircle>(), log);
            changed += SetRef(so, "_recordInnerRect", inner.GetComponent<RectTransform>(), log);

            var memo = Find("MemoButton");
            if (memo != null)
            {
                changed += SetRef(so, "_noteLabel",
                    memo.GetComponentInChildren<TextMeshProUGUI>(), log);
                changed += SetRef(so, "_noteJuice",
                    memo.GetComponent<UIButtonJuice>(), log);
            }
            so.ApplyModifiedProperties();
        }

        // --- 7. 削除ボタンの文字 ---------------------------------------
        var delText = Find("DeleteButton")?.GetComponentInChildren<TextMeshProUGUI>();
        if (delText != null && delText.text != "削除")
        {
            Undo.RecordObject(delText, "label");
            delText.text = "削除";
            EditorUtility.SetDirty(delText);
            log.AppendLine("  削除ボタンの文字を「削除」にした");
            changed++;
        }

        if (changed > 0)
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

        Debug.Log(changed == 0
            ? "撮影画面のUI: 直すところはありませんでした"
            : $"撮影画面のUI: {changed} 箇所を直しました\n{log}"
              + "\nシーンを保存してください (Ctrl+S)");
    }

    // ------------------------------------------------------------

    private static System.Collections.Generic.IEnumerable<Transform> AllTransforms()
    {
        return Object.FindObjectsByType<Transform>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
    }

    private static GameObject Find(string name)
    {
        foreach (var t in AllTransforms())
            if (t.name == name) return t.gameObject;
        return null;
    }

    private static int ReplaceWithCircle(string name, Color color, float roundness,
                                         float ringWidth, bool raycast,
                                         System.Text.StringBuilder log)
    {
        var go = Find(name);
        if (go == null)
        {
            log.AppendLine($"  {name} が見つかりません");
            return 0;
        }

        int changed = 0;

        // Graphic は1つしか付けられないので、先に Image を消す
        var image = go.GetComponent<Image>();
        if (image != null)
        {
            Undo.DestroyObjectImmediate(image);
            log.AppendLine($"  {name}: Image を外した");
            changed++;
        }

        var circle = go.GetComponent<UICircle>();
        if (circle == null)
        {
            circle = Undo.AddComponent<UICircle>(go);
            log.AppendLine($"  {name}: UICircle を足した");
            changed++;
        }

        Undo.RecordObject(circle, "circle");
        circle.color = color;
        circle.raycastTarget = raycast;

        var so = new SerializedObject(circle);
        so.FindProperty("_roundness").floatValue = roundness;
        so.FindProperty("_ringWidth").floatValue = ringWidth;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(circle);

        return changed;
    }

    private static int FixTargetGraphic(string name, System.Text.StringBuilder log)
    {
        var go = Find(name);
        var button = go != null ? go.GetComponent<Button>() : null;
        var circle = go != null ? go.GetComponent<UICircle>() : null;
        if (button == null || circle == null) return 0;
        if (button.targetGraphic == circle) return 0;

        Undo.RecordObject(button, "target graphic");
        button.targetGraphic = circle;
        EditorUtility.SetDirty(button);
        log.AppendLine($"  {name}: Target Graphic を UICircle にした");
        return 1;
    }

    private static int SetOnClick(string name, UnityAction action,
                                  System.Text.StringBuilder log)
    {
        var go = Find(name);
        var button = go != null ? go.GetComponent<Button>() : null;
        if (button == null) return 0;

        // すでに正しければ触らない
        if (button.onClick.GetPersistentEventCount() == 1
            && button.onClick.GetPersistentMethodName(0) == action.Method.Name
            && ReferenceEquals(button.onClick.GetPersistentTarget(0), action.Target))
            return 0;

        Undo.RecordObject(button, "onclick");
        for (int i = button.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
            UnityEventTools.RemovePersistentListener(button.onClick, i);

        UnityEventTools.AddPersistentListener(button.onClick, action);
        EditorUtility.SetDirty(button);
        log.AppendLine($"  {name}: OnClick を {action.Method.Name}() にした");
        return 1;
    }

    private static int SetRef(SerializedObject so, string field, Object value,
                              System.Text.StringBuilder log)
    {
        var p = so.FindProperty(field);
        if (p == null || value == null) return 0;
        if (p.objectReferenceValue == value) return 0;

        p.objectReferenceValue = value;
        log.AppendLine($"  CaptureButtons.{field} をつないだ");
        return 1;
    }
}
