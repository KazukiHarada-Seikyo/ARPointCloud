// 特徴点を「光の粒」として描く。
//
// 点をそのまま描くと1画素にしかならないので、1点につき四角形を4頂点で作り、
// 頂点シェーダで画面上の大きさを一定に保つ。これで遠くの点も潰れない。
//
// 頂点カラー = 色と濃さ (スクリプト側で新しさに応じて変える)
// uv         = 四隅 (-1〜1)。中心からの距離で丸く抜く
// uv2.x      = その点だけの大きさの倍率 (現れた瞬間だけ大きくする)
//
// ※ Shader.Find で実行時に探すため、Project Settings → Graphics →
//    Always Included Shaders に登録しないとビルドで削除される。

Shader "ARPointCloud/PointPreview"
{
    Properties
    {
        _PointSize ("点の大きさ (画素)", Range(2, 40)) = 9
        _CoreSharpness ("芯の締まり", Range(1, 8)) = 3
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        // 屋外の明るい映像の上でも飛ばないよう、加算ではなく通常の合成にする
        Blend SrcAlpha OneMinusSrcAlpha
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
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);

                // 画面上の大きさを一定に保つ。
                // クリップ空間は w で割られるので、先に w を掛けておく
                float size = _PointSize * v.uv2.x;
                float2 offset = v.uv * size;
                offset.x *= 2.0 / _ScreenParams.x;
                offset.y *= 2.0 / _ScreenParams.y;
                o.vertex.xy += offset * o.vertex.w;

                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float d = length(i.uv);
                clip(1.0 - d);

                // 外へ向かってなだらかに消す。芯だけ白く残すと粒に見える
                float falloff = pow(saturate(1.0 - d), _CoreSharpness);
                float core = pow(saturate(1.0 - d * 2.2), 4.0);

                fixed3 rgb = lerp(i.color.rgb, fixed3(1, 1, 1), core * 0.8);
                return fixed4(rgb, i.color.a * falloff);
            }
            ENDCG
        }
    }

    Fallback Off
}
