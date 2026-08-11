// UI用の円・角丸・リングを計算で描く。
//
// スプライトを引き伸ばすとギザギザになるので、画素ごとに形の内外を判定する。
// fwidth で1画素あたりの変化量を取るため、どんな大きさでも輪郭が滑らかになる。
//
// _Roundness = 1    … 円
// _Roundness = 0.35 … 角丸の四角
// _Roundness = 0    … 四角
// _RingWidth = 0    … 塗りつぶし
// _RingWidth > 0    … その太さのリング
//
// 丸と角丸が同じ式なので、_Roundness を動かせば録画ボタンの
// 「丸 → 四角」がなめらかに変形する。
//
// マテリアルは UICircle.cs が実行時に作る。.mat は不要。

Shader "ARPointCloud/UICircle"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Roundness ("角の丸み (1=円)", Range(0, 1)) = 1
        _RingWidth ("リングの太さ (0=塗りつぶし)", Range(0, 0.5)) = 0
        _Softness ("輪郭のなめらかさ", Range(0.5, 3)) = 1.2

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _Roundness;
            float _RingWidth;
            float _Softness;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            // 角丸四角までの符号付き距離。r=1 で円になる
            float sdRoundBox(float2 p, float r)
            {
                float2 q = abs(p) - (1.0 - r);
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 中心を原点にした -1〜1 の座標
                float2 p = (i.uv - 0.5) * 2.0;

                float r = clamp(_Roundness, 0.0001, 1.0);
                float d = sdRoundBox(p, r);

                // 1画素分の距離。拡大率が変わっても滑らかさが一定になる
                float aa = max(fwidth(d) * _Softness, 0.0001);

                float alpha = 1.0 - smoothstep(-aa, aa, d);

                if (_RingWidth > 0.0001)
                {
                    // 内側をくり抜く。距離を太さ分ずらすだけでよい
                    float inner = sdRoundBox(p, r) + _RingWidth * 2.0;
                    alpha *= smoothstep(-aa, aa, inner);
                }

                fixed4 c = i.color;
                c.a *= alpha;
                clip(c.a - 0.002);
                return c;
            }
            ENDCG
        }
    }

    Fallback Off
}
