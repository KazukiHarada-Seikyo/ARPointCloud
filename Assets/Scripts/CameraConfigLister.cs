using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class CameraConfigLister : MonoBehaviour
{
    [SerializeField] private ARCameraManager _cameraManager;

    /// <summary>空文字のときは通常表示。中身があるときは画面がこちらに切り替わる</summary>
    public string Report { get; private set; } = "";

    /// <summary>押すたびに、一覧表示と通常表示を行き来する</summary>
    public void ToggleConfigList()
    {
        if (!string.IsNullOrEmpty(Report))
        {
            Report = "";
            return;
        }

        var sb = new StringBuilder();

        if (_cameraManager == null)
        {
            Report = "ARCameraManager が未設定です";
            return;
        }

        var current = _cameraManager.currentConfiguration;

        using (var configs = _cameraManager.GetConfigurations(Allocator.Temp))
        {
            sb.AppendLine($"映像設定 {configs.Length} 種");

            if (configs.Length == 0)
            {
                sb.AppendLine("(映像がまだ流れていません。数秒待って再度押してください)");
            }

            foreach (var c in configs)
            {
                bool isCurrent = current.HasValue && current.Value == c;
                string fps = c.framerate.HasValue ? $"{c.framerate.Value}fps" : "-";
                sb.AppendLine($"{(isCurrent ? "→" : "  ")}{c.width}x{c.height} {fps}");
            }
        }

        sb.AppendLine("--------------------");

        // 内部パラメータは、映像が1枚届いてからでないと取れない
        if (_cameraManager.TryGetIntrinsics(out XRCameraIntrinsics k))
        {
            sb.AppendLine($"焦点距離 fx={k.focalLength.x:F1} fy={k.focalLength.y:F1}");
            sb.AppendLine($"主点     cx={k.principalPoint.x:F1} cy={k.principalPoint.y:F1}");
            sb.AppendLine($"基準解像度 {k.resolution.x}x{k.resolution.y}");
        }
        else
        {
            sb.AppendLine("内部パラメータ取得不可");
        }

        Report = sb.ToString();
        Debug.Log(Report);
    }

    /// <summary>いちばん画素数の多い設定に切り替える。一覧を見てから押すこと</summary>
    public void UseHighestResolution()
    {
        if (_cameraManager == null) return;

        XRCameraConfiguration? best = null;
        int bestPixels = -1;

        using (var configs = _cameraManager.GetConfigurations(Allocator.Temp))
        {
            foreach (var c in configs)
            {
                int pixels = c.width * c.height;
                if (pixels > bestPixels)
                {
                    bestPixels = pixels;
                    best = c;
                }
            }
        }

        if (best.HasValue)
        {
            _cameraManager.currentConfiguration = best.Value;
            Report = $"切替 {best.Value.width}x{best.Value.height}\n" +
                     "(トラッキングが一度リセットされます)";
            Debug.Log(Report);
        }
        else
        {
            Report = "切替できる設定がありません";
        }
    }
}