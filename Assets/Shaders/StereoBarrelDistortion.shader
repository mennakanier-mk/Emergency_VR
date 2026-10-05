Shader "EmergencyVR/StereoBarrelDistortion"
{
    // Presents one eye's RenderTexture with barrel distortion and a round mask, which is what
    // gives the circular "porthole" look in every Cardboard-style game.
    //
    // Why the image has to be bent: the viewer's lens pincushions whatever it magnifies, so
    // straight lines bow inward. Pre-bending the image the opposite way (barrel) cancels it,
    // and the world looks straight through the lens. Without this, side-by-side still "works"
    // but the periphery stretches and the two images fight to fuse.
    //
    // K1 / K2 are the usual radial polynomial terms. Cardboard v2 profiles sit near
    // K1 0.34 / K2 0.55; cheap plastic viewers are usually gentler. They are exposed because
    // the right values depend on your lenses, and there is no QR code to read them from.

    Properties
    {
        _MainTex        ("Eye Texture", 2D) = "black" {}
        _K1             ("Distortion K1", Range(-1, 1)) = 0.22
        _K2             ("Distortion K2", Range(-1, 1)) = 0.10
        _Zoom           ("Zoom", Range(0.5, 1.5)) = 0.90
        _CenterOffset   ("Lens Centre Offset X", Range(-0.3, 0.3)) = 0
        _MaskRadius     ("Mask Radius", Range(0.5, 1.5)) = 1.0
        _MaskSoftness   ("Mask Softness", Range(0.001, 0.3)) = 0.02
        _Background     ("Background", Color) = (0, 0, 0, 1)
    }

    SubShader
    {
        Tags
        {
            "Queue"            = "Overlay"
            "RenderType"       = "Opaque"
            "IgnoreProjector"  = "True"
            "PreviewType"      = "Plane"
        }

        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                float4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float  _K1;
            float  _K2;
            float  _Zoom;
            float  _CenterOffset;
            float  _MaskRadius;
            float  _MaskSoftness;
            fixed4 _Background;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Screen space for this eye, centred on the lens axis rather than the rect.
                float2 c = (i.uv - 0.5) * 2.0;
                c.x -= _CenterOffset;

                float r2 = dot(c, c);

                // Radial polynomial. Positive K pushes the sample outward, which squeezes the
                // periphery on screen - the barrel the lens then undoes.
                float scale = 1.0 + _K1 * r2 + _K2 * r2 * r2;

                float2 src = c * scale * _Zoom * 0.5 + 0.5;

                // Anything the warp pulls in from outside the render texture is dead space.
                if (src.x < 0.0 || src.x > 1.0 || src.y < 0.0 || src.y > 1.0)
                    return _Background;

                fixed4 col = tex2D(_MainTex, src);

                // Round mask - the porthole. Softened so the rim is not a hard jaggy circle.
                float r = sqrt(r2);
                float mask = 1.0 - smoothstep(_MaskRadius - _MaskSoftness,
                                              _MaskRadius + _MaskSoftness, r);

                return lerp(_Background, col, mask);
            }
            ENDCG
        }
    }

    FallBack "Unlit/Texture"
}
