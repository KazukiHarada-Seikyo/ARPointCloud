using System.Collections;
using System.Text;
using Google.XR.ARCoreExtensions;
using UnityEngine;

public class VpsCoverageChecker : MonoBehaviour
{
    [System.Serializable]
    public class Site
    {
        public string name;
        public double latitude;
        public double longitude;
    }

    [SerializeField] private Site[] _sites;

    public string Report { get; private set; } = "未確認";

    public void CheckAll() => StartCoroutine(CheckAllRoutine());

    private IEnumerator CheckAllRoutine()
    {
        var sb = new StringBuilder();

        foreach (var site in _sites)
        {
            Report = sb + $"{site.name}: 問い合わせ中...";

            // 端末が今どこにいるかとは無関係に、指定した座標を調べられる
            var promise = AREarthManager.CheckVpsAvailabilityAsync(
                site.latitude, site.longitude);

            yield return promise;

            sb.AppendLine($"{site.name}: {promise.Result}");
            Report = sb.ToString();
            Debug.Log($"VPS {site.name} -> {promise.Result}");
        }
    }
}