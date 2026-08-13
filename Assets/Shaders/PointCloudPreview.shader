// 特徴点を「光の粒」として描く。
//
// 点をそのまま描くと1画素にしかならないので、1点につき四角形を4頂点で作り、
// 頂点シェーダで画面上の大きさを一定に保つ。これで遠くの点も潰れない。
//
// --------------------------------------------------------------------
// 演出をシェーダ側でやる理由
//
// 以前は「現れてから落ち着くまで」の色と大きさをスクリプトが毎回計算し、
// 0.1秒ごとにメッシュを作り直していた。点が1万個あると、動きを見せている
// あいだじゅうCPUが4万頂点を書き直し続けることになる。
//
// いまは点ごとに「生まれた時刻」と「乱数の種」だけを持たせ、
// 時間の関数はすべてGPUで解く。**メッシュは点が増減したときだけ作り直す。**
// 撮影中に端末が熱を持つと1フレームの余裕が無くなるので、ここは効く。
// --------------------------------------------------------------------
//
// 頂点の中身
//   POSITION   世界座標
//   uv         四隅 (-1〜1)。中心からの距離で丸く抜く
//   uv2.x      生まれた時刻 (Time.time)
//   uv2.y      その点だけの乱数 0〜1。またたきの位相をずらす
//   color      点ごとの色味。いまは白（将来の色分け用に残してある）
//
// ※ Shader.Find で実行時に探すため、Project Settings → Graphics →
//    Always Included Shaders に登録しないとビルドで削除される。

Shader "ARPointCloud/PointPreview"
{
    Properties
    {
        _PointSize ("点の大きさ (画素)", Range(2, 40)) = 9
        _CoreSharpness ("芯の締まり", Range(1, 8)) = 3

        [Header(Neon)]
        _Glow ("ネオンの強さ", Range(0, 1)) = 0.55
        _HaloSize ("光のにじみ", Range(0, 1)) = 0.6

        [Header(Twinkle)]
        _TwinkleAmount ("またたきの深さ", Range(0, 1)) = 0.35
        _TwinkleSpeed ("またたきの速さ", Range(0, 12)) = 3.0

        [Header(Birth)]
        _FreshColor ("現れた直後の色", Color) = (1, 1, 1, 0.95)
        _SettledColor ("落ち着いたあとの色", Color) = (0.62, 0.82, 1, 0.42)
        _SettleSeconds ("落ち着くまでの秒数", Range(0.05, 4)) = 0.7
        _PopScale ("現れた瞬間の倍率", Range(1, 4)) = 1.5

        [Header(Scan wave)]
        _WaveSpeed ("波の速さ (m/s)", Range(0.5, 20)) = 4.0
        _WaveWidth ("波の厚み (m)", Range(0.05, 3)) = 0.55
        _WaveStrength ("波の強さ", Range(0, 3)) = 1.2
        _WaveRange ("波が届く距離 (m)", Range(1, 40)) = 12
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        // 前倍算アルファ。_Glow = 0 なら通常の合成とまったく同じ絵になり、
        // 上げるほど芯だけが加算に寄る。
        // 屋外の明るい映像の上で全体を加算にすると白飛びするので、
        // 「にじみは通常合成、芯だけ加算」という配分にしてある
        Blend One OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Lighting Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _PointSize;
            float _CoreSharpness;
            float _Glow;
            float _HaloSize;
            float _TwinkleAmount;
            float _TwinkleSpeed;
            fixed4 _FreshColor;
            fixed4 _SettledColor;
            float _SettleSeconds;
            float _PopScale;

            // 波は同時に2本まで重ねられる。xyz = 出た場所, w = 出た時刻。
            // スクリプトが Material に入れる
            float4 _Wave0;
            float4 _Wave1;
            float _WaveSpeed;
            float _WaveWidth;
            float _WaveStrength;
            float _WaveRange;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                float2 uv2    : TEXCOORD1;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv     : TEXCOORD0;
                fixed4 color  : COLOR;
                float  wave   : TEXCOORD1;   // 波にどれだけ照らされているか
                float  sparkle: TEXCOORD2;   // いまどれだけ瞬いているか
            };

            // 1本の波が、その点をどれだけ照らしているか (0〜1)
            float WaveAt(float4 wave, float3 world)
            {
                // w = 0 は「まだ波が出ていない」印
                if (wave.w <= 0.0) return 0.0;

                float elapsed = _Time.y - wave.w;
                if (elapsed < 0.0) return 0.0;

                float radius = elapsed * _WaveSpeed;
                if (radius > _WaveRange) return 0.0;

                float d = distance(world, wave.xyz);

                // 波の輪から離れるほど暗くする。ガウス型にすると縁が硬くならない
                float t = (d - radius) / max(0.01, _WaveWidth);
                float ring = exp(-t * t);

                // 遠くまで行った波は薄れて消える
                float fade = saturate(1.0 - radius / _WaveRange);
                return ring * fade * fade;
            }

            v2f vert (appdata v)
            {
                v2f o;

                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.vertex = UnityObjectToClipPos(v.vertex);

                float bornAt = v.uv2.x;
                float seed   = v.uv2.y;

                // 0 = 現れた瞬間, 1 = 落ち着いた
                float age = saturate((_Time.y - bornAt) / max(0.01, _SettleSeconds));
                float ease = age * age * (3.0 - 2.0 * age);      // なめらかに

                // またたき。点ごとに位相をずらすので、群れがざわざわ見える。
                //
                // ただの sin だと全体がゆっくり明滅するだけで、
                // 「キラキラ」には見えない。累乗して山を尖らせると、
                // ふだんは静かで時々ぱっと光る = 星の瞬きになる。
                // 1より下げない(消える点があると数を見誤る)
                float phase = _Time.y * _TwinkleSpeed + seed * 6.2831853;
                float s = sin(phase) * 0.5 + 0.5;
                float sparkle = pow(s, 5.0) * ease;
                float twinkle = 1.0 + _TwinkleAmount * sparkle;
                o.sparkle = sparkle;

                float wave = saturate(WaveAt(_Wave0, world) + WaveAt(_Wave1, world));
                o.wave = wave;

                // 大きさ: 生まれたては大きく、またたきで膨らみ、波が来ると膨らむ
                float scale = lerp(_PopScale, 1.0, ease)
                            * twinkle
                            * (1.0 + wave * _WaveStrength * 0.6);

                float size = _PointSize * scale;
                float2 offset = v.uv * size;
                offset.x *= 2.0 / _ScreenParams.x;
                offset.y *= 2.0 / _ScreenParams.y;
                o.vertex.xy += offset * o.vertex.w;

                fixed4 c = lerp(_FreshColor, _SettledColor, ease);
                c.rgb *= v.color.rgb;                 // 点ごとの色味 (いまは白)
                c.a *= v.color.a;

                // 波が通るところは明るく、少し白へ寄せる
                c.rgb = lerp(c.rgb, fixed3(1, 1, 1), wave * 0.5);
                c.a = saturate(c.a * (1.0 + wave * _WaveStrength));

                o.color = c;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float d = length(i.uv);
                clip(1.0 - d);

                // 芯とにじみを別々に作る。
                // 芯 = 小さく硬い点、にじみ = そのまわりに広がる光
                float core = pow(saturate(1.0 - d * 2.2), 4.0);
                float halo = pow(saturate(1.0 - d), lerp(_CoreSharpness * 2.0,
                                                         _CoreSharpness, _HaloSize));

                float a = saturate(i.color.a * halo);

                // 前倍算。ここまでは通常の合成と同じ絵
                fixed3 rgb = i.color.rgb * a;

                // 芯だけを加算で足す。ここがネオンに見える部分。
                // 瞬いている粒と、波に照らされている粒はもうひと押し光らせる
                float boost = _Glow * (1.0 + i.sparkle * 1.2 + i.wave * 1.5);
                rgb += i.color.rgb * core * boost * i.color.a;

                return fixed4(rgb, a);
            }
            ENDCG
        }
    }

    Fallback Off
}
