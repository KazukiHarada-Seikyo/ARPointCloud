// UI用の円・リングを計算で描く。
//
// Unity内蔵の Knob スプライトは32px程度しかなく、200pxのボタンに使うと
// 輪郭がギザギザになる。画像を引き伸ばす代わりに、画素ごとに中心からの
// 距離を測って塗り分ければ、どんな大きさでも輪郭が滑らかになる。
//
// _RingWidth = 0     … 塗りつぶした円
// _RingWidth = 0.06  … 太さ6%のリング（録画ボタンの白い輪）
//
// マテリアルは UICircleImage.cs が実行時に作る。.mat を用意する必要はない。

Shader "ARPointCloud/UICircle"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
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

            fixed4 frag (v2f i) : SV_Target
            {
                // 中心からの距離。0 が中心、1 が外周
                float d = length(i.uv - 0.5) * 2.0;

                // fwidth で「この画素1つ分の距離の変化量」を得る。
                // これを使うと、拡大率に関係なく同じ滑らかさになる
                float aa = max(fwidth(d) * _Softness, 0.0001);

                float alpha = 1.0 - smoothstep(1.0 - aa, 1.0, d);

                if (_RingWidth > 0.0001)
                {
                    float innerEdge = 1.0 - _RingWidth * 2.0;
                    alpha *= smoothstep(innerEdge - aa, innerEdge, d);
                }

                fixed4 c = i.color;
                c.a *= alpha;
                clip(c.a - 0.001);
                return c;
            }
            ENDCG
        }
    }

    Fallback Off
}
