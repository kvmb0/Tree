float ScreenAspect;
float3 Padding;

struct PARTICLE
{
  float2 position : POSITION;
  float2 velocity : VELOCITY;
  float2 ageAndSize : AGE;
  uint color : COLOR;
};

struct VS_OUT
{
  float4 position : SV_POSITION;
  float age : AGE;
  float size : SIZE;
  float4 color : COLOR;
};

struct GS_OUT
{
  float4 position : SV_POSITION;
  float2 uv : TEXCOORD0;
  float age : TEXCOORD1;
  float4 color : COLOR;
};

VS_OUT VS_Particle(PARTICLE input)
{
  VS_OUT output;
  float2 position = input.position + input.velocity * input.ageAndSize.x;
  position.y -= 0.65 * input.ageAndSize.x * input.ageAndSize.x;
  output.position = float4(position.x * 2 - 1, position.y * 2 - 1, 0, 1);
  output.age = input.ageAndSize.x;
  output.size = input.ageAndSize.y * (1 - 0.4 * saturate(input.ageAndSize.x / 0.62));
  output.color = float4(
    (input.color >> 24 & 0xff) / 255.0,
    (input.color >> 16 & 0xff) / 255.0,
    (input.color >> 8 & 0xff) / 255.0,
    (input.color & 0xff) / 255.0);
  return output;
}

[maxvertexcount(4)]
void GS_Particle(point VS_OUT input[1], inout TriangleStream<GS_OUT> outputStream)
{
  float2 center = input[0].position.xy;
  float2 halfSize = float2(input[0].size * ScreenAspect * 0.55, input[0].size * 2.2);
  GS_OUT output;
  output.age = input[0].age;
  output.color = input[0].color;

  output.uv = float2(-1, -1);
  output.position = float4(center + float2(-halfSize.x, -halfSize.y), 0, 1);
  outputStream.Append(output);
  output.uv = float2(-1, 1);
  output.position = float4(center + float2(-halfSize.x, halfSize.y), 0, 1);
  outputStream.Append(output);
  output.uv = float2(1, -1);
  output.position = float4(center + float2(halfSize.x, -halfSize.y), 0, 1);
  outputStream.Append(output);
  output.uv = float2(1, 1);
  output.position = float4(center + float2(halfSize.x, halfSize.y), 0, 1);
  outputStream.Append(output);
  outputStream.RestartStrip();
}

float4 PS_Particle(GS_OUT input) : SV_Target
{
  float radius = dot(input.uv, input.uv);
  clip(1 - radius);
  float spark = saturate(1 - radius);
  float fade = saturate(1 - input.age / 0.62);
  float glow = pow(spark, 1.25) * fade;
  float core = pow(spark, 3.0);
  float3 sparkColor = saturate(input.color.rgb * (1.25 + core * 0.75));
  return float4(sparkColor, input.color.a * glow);
}
